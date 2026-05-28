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
/// reinterprets the low 11 bits as offset minutes, lands outside the legal
/// ±14 hour range about 18% of the time, and throws
/// <c>ArgumentOutOfRangeException</c> on every read.
///
/// Affected columns: <c>CatalogTitles.FirstSeenAt</c>,
/// <c>CatalogTitles.LastSeenAt</c>, <c>CatalogLinks.CreatedAt</c>
/// (the heavy one — millions of rows).
///
/// Strategy: <b>one SQL <c>UPDATE</c> statement per column</b>. The
/// encoding math is closed-form for UTC (every value Hydracker wrote was
/// UTC since <c>DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()</c> discards
/// the offset) and SQLite can do the entire 2.3M-row re-encode in a single
/// transaction in under a minute. The previous batched-loop approach did a
/// full table scan per 5k-row batch and took 30+ minutes while starving
/// every other read of the SQLite cache.
///
/// Runs as a background hosted service so a fresh deploy doesn't block
/// startup. Idempotent — once a column has no rows below the magnitude
/// threshold, the UPDATE is a no-op.
/// </summary>
public sealed class DateTimeOffsetRepairService : BackgroundService
{
    // Binary-encoded values for any plausible date are >> 10^17.
    // Unix-ms values for any date from 1970-2100 are < 10^13.
    private const long UnixMsCeiling = 1_000_000_000_000_000L; // 10^15

    // Constants of the encoding identity for UTC offsets, derived from
    // EncodeBinary's formula:
    //
    //   encoded = ((Ticks / 1000) << 11) | ((offsetMin + 1024) & 0x7FF)
    //
    // For DateTimeOffset.FromUnixTimeMilliseconds(unixMs) the offset is
    // always TimeSpan.Zero, so offsetMin = 0 and (offsetMin + 1024) & 0x7FF = 1024.
    //
    //   Ticks = UnixEpochTicks + unixMs * TicksPerMillisecond
    //         = 621355968000000000 + unixMs * 10000
    //   Ticks / 1000 = 621355968000000 + unixMs * 10
    //
    // The low 11 bits of (Ticks / 1000) << 11 are zero, so OR-ing 1024
    // is equivalent to addition:
    //
    //   encoded = (621355968000000 + unixMs * 10) * 2048 + 1024
    //
    // Used directly in the UPDATE statements below.
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
        // Let the API serve requests first — repair is best-effort.
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        try
        {
            var totalFixed = 0L;
            foreach (var (table, col) in Columns)
            {
                if (stoppingToken.IsCancellationRequested) return;
                totalFixed += await RepairColumnAsync(table, col, stoppingToken);
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

    private async Task<long> RepairColumnAsync(string table, string col, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        await conn.OpenAsync(ct);

        var ceiling = UnixMsCeiling.ToString(CultureInfo.InvariantCulture);

        // Single-statement re-encode in one implicit transaction. SQLite
        // holds a write lock for the duration; readers proceed against the
        // WAL snapshot. Concurrent writers (e.g. the enricher) queue for the
        // length of the UPDATE — acceptable for a one-shot migration.
        using var upd = conn.CreateCommand();
        upd.CommandText =
            $"UPDATE {table} " +
            $"SET {col} = ({UnixEpochTicksDiv1000} + {col} * 10) * 2048 + {UtcOffsetEncodedBits} " +
            $"WHERE {col} > 0 AND {col} < {ceiling}";
        // Generous timeout: 2.3M-row update on shared-cpu-1x typically lands
        // around 30-60s. Default 30s would trip mid-statement.
        upd.CommandTimeout = (int)TimeSpan.FromMinutes(10).TotalSeconds;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var rows = await upd.ExecuteNonQueryAsync(ct);
        sw.Stop();

        if (rows > 0)
            _log.LogInformation("DateTimeOffset repair: {Table}.{Col} re-encoded {Count} row(s) in {Elapsed}.",
                table, col, rows, sw.Elapsed);
        return rows;
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
