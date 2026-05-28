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
/// EF Core's <c>DateTimeOffsetToBinaryConverter</c> packs the value as
/// <c>((ticks/1000) &lt;&lt; 11) | ((offsetMinutes + 1024) &amp; 0x7FF)</c>.
/// Reading a Unix-ms long back through that converter reinterprets the low
/// 11 bits as offset minutes, lands outside the legal ±14 hour range about
/// 18% of the time, and throws <c>ArgumentOutOfRangeException</c>.
///
/// Affected columns: <c>CatalogTitles.FirstSeenAt</c>,
/// <c>CatalogTitles.LastSeenAt</c>, <c>CatalogLinks.CreatedAt</c>.
///
/// Strategy: one SQL UPDATE per column with a WHERE clause that catches
/// <b>both</b> failure modes:
///   1. Raw Unix-ms values (magnitude &lt; 10^15).
///   2. Already-encoded values whose low 11 bits decode to an offset
///      outside ±840 minutes (would throw on read). These shouldn't exist
///      under normal write paths, but if a previous run of this service
///      somehow produced one, this clause heals it.
///
/// The encoding math is closed-form for UTC values (the only kind Hydracker
/// wrote, since <c>DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()</c>
/// discards offset), so SQLite can do the re-encode entirely in a single
/// UPDATE statement per column.
///
/// Runs as a background hosted service after a short startup delay.
/// Idempotent — once everything's clean, subsequent boots match zero rows.
/// </summary>
public sealed class DateTimeOffsetRepairService : BackgroundService
{
    // Unix-ms values for any plausible date are well under 10^13;
    // binary-encoded values for any plausible date are well over 10^17.
    // A 10^15 ceiling cleanly separates them.
    private const long UnixMsCeiling = 1_000_000_000_000_000L;

    // The encoded-form WHERE has to detect rows whose low 11 bits decode
    // out of range. EF stores `(offsetMin + 1024) & 0x7FF`; legal offsets
    // are -840..+840 ⇒ stored bits 184..1864. Anything else throws.
    private const long OffsetBitMin = 184;
    private const long OffsetBitMax = 1864;

    private const long UnixEpochTicksDiv1000 = 621_355_968_000_000L;
    private const long UtcOffsetEncodedBits = 1024L;

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
        // Loudly announce the service started, so log triage can confirm it
        // ran at all without needing to grep startup banner output.
        _log.LogInformation("DateTimeOffset repair: service scheduled (5s startup delay).");

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
        catch (OperationCanceledException) { return; }

        var grandTotal = 0L;
        try
        {
            foreach (var (table, col) in Columns)
            {
                if (stoppingToken.IsCancellationRequested) return;
                grandTotal += await RepairColumnAsync(table, col, stoppingToken);
            }

            // Final pass: log the leftover counts so a human reading logs
            // can verify success without an admin endpoint.
            await ReportLeftoversAsync(stoppingToken);

            _log.LogInformation("DateTimeOffset repair: complete. Re-encoded {Total} row(s) across all columns.", grandTotal);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogError(ex, "DateTimeOffset repair crashed.");
        }
    }

    private async Task<long> RepairColumnAsync(string table, string col, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        await conn.OpenAsync(ct);

        var ceiling = UnixMsCeiling.ToString(CultureInfo.InvariantCulture);
        var (unixMs, badBits) = await CountAsync(conn, table, col, ceiling, ct);

        if (unixMs == 0 && badBits == 0)
        {
            _log.LogInformation("DateTimeOffset repair: {Table}.{Col} clean (0 bad rows).", table, col);
            return 0;
        }

        _log.LogInformation("DateTimeOffset repair: {Table}.{Col} has {Unix} unix-ms row(s) + {Bad} bad-offset row(s). Repairing…",
            table, col, unixMs, badBits);

        // 1) Re-encode raw Unix-ms values using the closed-form identity.
        var swA = System.Diagnostics.Stopwatch.StartNew();
        var fixedUnixMs = await ExecAsync(conn, ct,
            $"UPDATE {table} " +
            $"SET {col} = ({UnixEpochTicksDiv1000} + {col} * 10) * 2048 + {UtcOffsetEncodedBits} " +
            $"WHERE {col} > 0 AND {col} < {ceiling}");
        swA.Stop();

        // 2) Heal any row whose low 11 bits decode to an out-of-range offset
        //    while the ticks portion looks plausible. Force-reset offset bits
        //    to UTC (1024). The "wall clock" portion (high bits) is preserved.
        //    These shouldn't exist normally; this is belt-and-suspenders.
        var swB = System.Diagnostics.Stopwatch.StartNew();
        var fixedBadBits = await ExecAsync(conn, ct,
            $"UPDATE {table} " +
            $"SET {col} = ({col} & ~2047) | {UtcOffsetEncodedBits} " +
            $"WHERE {col} >= {ceiling} " +
            $"  AND (({col} & 2047) < {OffsetBitMin} OR ({col} & 2047) > {OffsetBitMax})");
        swB.Stop();

        _log.LogInformation(
            "DateTimeOffset repair: {Table}.{Col} re-encoded {Unix} unix-ms in {EA}, healed {Bad} bad-offset in {EB}.",
            table, col, fixedUnixMs, swA.Elapsed, fixedBadBits, swB.Elapsed);

        return fixedUnixMs + fixedBadBits;
    }

    private static async Task<long> ExecAsync(SqliteConnection conn, CancellationToken ct, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = (int)TimeSpan.FromMinutes(10).TotalSeconds;
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<(long UnixMs, long BadBits)> CountAsync(
        SqliteConnection conn, string table, string col, string ceiling, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"SELECT " +
            $"  SUM(CASE WHEN {col} > 0 AND {col} < {ceiling} THEN 1 ELSE 0 END), " +
            $"  SUM(CASE WHEN {col} >= {ceiling} " +
            $"           AND (({col} & 2047) < {OffsetBitMin} OR ({col} & 2047) > {OffsetBitMax}) " +
            $"        THEN 1 ELSE 0 END) " +
            $"FROM {table}";
        cmd.CommandTimeout = 300;
        using var rdr = await cmd.ExecuteReaderAsync(ct);
        if (!await rdr.ReadAsync(ct)) return (0, 0);
        long unixMs = rdr.IsDBNull(0) ? 0 : rdr.GetInt64(0);
        long badBits = rdr.IsDBNull(1) ? 0 : rdr.GetInt64(1);
        return (unixMs, badBits);
    }

    private async Task ReportLeftoversAsync(CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        await conn.OpenAsync(ct);
        var ceiling = UnixMsCeiling.ToString(CultureInfo.InvariantCulture);
        var total = 0L;
        foreach (var (table, col) in Columns)
        {
            var (u, b) = await CountAsync(conn, table, col, ceiling, ct);
            total += u + b;
            if (u != 0 || b != 0)
                _log.LogWarning("DateTimeOffset repair: {Table}.{Col} STILL has {Unix} unix-ms + {Bad} bad-offset row(s) after repair.",
                    table, col, u, b);
        }
        if (total == 0)
            _log.LogInformation("DateTimeOffset repair: post-repair scan shows 0 bad rows. Database is clean.");
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
