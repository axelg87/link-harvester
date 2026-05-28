using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LinkHarvester.Persistence.Maintenance;

/// <summary>
/// One-shot repair for DateTimeOffset columns the Hydracker
/// <see cref="Catalog.CatalogIngestor"/> wrote via raw SQL as
/// <c>ToUnixTimeMilliseconds()</c>.
///
/// The schema uses EF Core's <c>DateTimeOffsetToBinaryConverter</c>, which
/// packs the value as <c>((ticks/1000) &lt;&lt; 11) | ((offsetMinutes + 1024)
/// &amp; 0x7FF)</c>. Reading a Unix-ms long back through that converter
/// reinterprets the low 11 bits as offset minutes, producing values outside
/// the DSM-legal ±14 hour range about 18% of the time — which is the
/// <c>System.ArgumentOutOfRangeException: Offset must be within plus or
/// minus 14 hours</c> error users hit on every imported title.
///
/// Affected columns:
///   <c>CatalogTitles.FirstSeenAt</c>, <c>CatalogTitles.LastSeenAt</c>,
///   <c>CatalogLinks.CreatedAt</c> (the heavy one — millions of rows).
///
/// Runs as a background hosted service so a fresh deploy with 2M+ stale
/// rows doesn't blow Fly's startup grace period. Idempotent — subsequent
/// boots find zero rows below the threshold and exit immediately.
/// </summary>
public sealed class DateTimeOffsetRepairService : BackgroundService
{
    // Binary-encoded values for any plausible date are >> 10^17.
    // Unix-ms values for any date from 1970-2100 are < 10^13.
    private const long UnixMsCeiling = 1_000_000_000_000_000L; // 10^15

    // Tune for SQLite WAL: keep transactions small enough that readers see
    // recent writes within seconds (the API can keep serving while we work).
    private const int BatchSize = 5_000;

    private static readonly (string Table, string Column)[] Columns =
    {
        ("CatalogTitles", "FirstSeenAt"),
        ("CatalogTitles", "LastSeenAt"),
        ("CatalogLinks",  "CreatedAt"),
    };

    private readonly IDbContextFactory<HarvesterDbContext> _factory;
    private readonly ILogger<DateTimeOffsetRepairService> _log;

    public DateTimeOffsetRepairService(
        IDbContextFactory<HarvesterDbContext> factory,
        ILogger<DateTimeOffsetRepairService> log)
    {
        _factory = factory;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the API serve requests first — repair is best-effort and runs
        // for as long as it takes.
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        try
        {
            var totalFixed = 0;
            foreach (var (table, col) in Columns)
            {
                if (stoppingToken.IsCancellationRequested) return;
                var fixedHere = await RepairColumnAsync(table, col, stoppingToken);
                totalFixed += fixedHere;
            }

            if (totalFixed == 0)
                _log.LogInformation("DateTimeOffset repair: database already clean.");
            else
                _log.LogInformation("DateTimeOffset repair: re-encoded {Count} row(s) total.", totalFixed);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogError(ex, "DateTimeOffset repair crashed; restart the app to retry.");
        }
    }

    private async Task<int> RepairColumnAsync(string table, string col, CancellationToken ct)
    {
        // Pre-count so we can log a meaningful progress %.
        long pending;
        await using (var db = await _factory.CreateDbContextAsync(ct))
        {
            var conn = (SqliteConnection)db.Database.GetDbConnection();
            await conn.OpenAsync(ct);
            using var count = conn.CreateCommand();
            count.CommandText =
                $"SELECT COUNT(*) FROM {table} WHERE {col} > 0 AND {col} < {UnixMsCeiling.ToString(CultureInfo.InvariantCulture)}";
            pending = (long)(await count.ExecuteScalarAsync(ct) ?? 0L);
        }

        if (pending == 0)
        {
            _log.LogDebug("DateTimeOffset repair: {Table}.{Col} clean (0 rows).", table, col);
            return 0;
        }

        _log.LogInformation("DateTimeOffset repair: {Table}.{Col} has {Count} stale row(s); starting batched re-encode.",
            table, col, pending);

        var fixedTotal = 0;
        while (!ct.IsCancellationRequested)
        {
            int fixedInBatch = await RepairBatchAsync(table, col, BatchSize, ct);
            if (fixedInBatch == 0) break;
            fixedTotal += fixedInBatch;
            _log.LogInformation("DateTimeOffset repair: {Table}.{Col} {Done}/{Total} ({Pct:0}%).",
                table, col, fixedTotal, pending, 100.0 * fixedTotal / Math.Max(1, pending));
        }

        return fixedTotal;
    }

    private async Task<int> RepairBatchAsync(string table, string col, int limit, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        await conn.OpenAsync(ct);

        var pending = new List<(long Id, long UnixMs)>(capacity: limit);
        using (var read = conn.CreateCommand())
        {
            read.CommandText =
                $"SELECT rowid, {col} FROM {table} " +
                $"WHERE {col} > 0 AND {col} < {UnixMsCeiling.ToString(CultureInfo.InvariantCulture)} " +
                $"LIMIT {limit}";
            using var rdr = await read.ExecuteReaderAsync(ct);
            while (await rdr.ReadAsync(ct))
                pending.Add((rdr.GetInt64(0), rdr.GetInt64(1)));
        }

        if (pending.Count == 0) return 0;

        using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        using (var upd = conn.CreateCommand())
        {
            upd.Transaction = tx;
            upd.CommandText = $"UPDATE {table} SET {col} = $v WHERE rowid = $r";
            var pV = upd.CreateParameter(); pV.ParameterName = "$v"; upd.Parameters.Add(pV);
            var pR = upd.CreateParameter(); pR.ParameterName = "$r"; upd.Parameters.Add(pR);
            foreach (var (id, unixMs) in pending)
            {
                var dto = DateTimeOffset.FromUnixTimeMilliseconds(unixMs);
                pV.Value = EncodeBinary(dto);
                pR.Value = id;
                await upd.ExecuteNonQueryAsync(ct);
            }
        }
        await tx.CommitAsync(ct);
        return pending.Count;
    }

    /// <summary>
    /// Mirrors EF Core's <c>DateTimeOffsetToBinaryConverter</c>:
    /// <c>((ticks / 1000) &lt;&lt; 11) | ((offsetMinutes + 1024) &amp; 0x7FF)</c>.
    /// Public so the ingestor can produce schema-compatible values directly.
    /// </summary>
    public static long EncodeBinary(DateTimeOffset dto)
    {
        var offsetMin = (long)dto.Offset.TotalMinutes;
        return ((dto.Ticks / 1000) << 11) | ((offsetMin + 1024) & 0x7FF);
    }
}
