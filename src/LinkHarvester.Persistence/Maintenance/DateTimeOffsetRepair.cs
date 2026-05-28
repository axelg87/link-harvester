using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LinkHarvester.Persistence.Maintenance;

/// <summary>
/// One-shot repair for DateTimeOffset columns that were either written as
/// raw <c>ToUnixTimeMilliseconds()</c> (Hydracker ingestor bug) or somehow
/// landed in an already-encoded shape whose low 11 bits decode to an
/// offset outside the legal ±840 minute range.
///
/// EF Core's <c>DateTimeOffsetToBinaryConverter</c> packs the value as
/// <c>((ticks/1000) &lt;&lt; 11) | ((offsetMinutes + 1024) &amp; 0x7FF)</c>;
/// reading a row whose low 11 bits sit outside [184..1864] throws
/// <c>ArgumentOutOfRangeException</c> on every materialise, which 500s the
/// endpoint regardless of which other columns are healthy.
///
/// Strategy: one SQL UPDATE per (table, column) for each failure mode.
/// The Unix-ms re-encode uses a closed-form identity that holds for the
/// UTC-only writes Hydracker did. The bad-offset heal preserves the high
/// bits (wall-clock ticks) and pins the offset to UTC.
/// </summary>
public sealed class DateTimeOffsetRepairService : BackgroundService
{
    private const long UnixMsCeiling = 1_000_000_000_000_000L;
    private const long OffsetBitMin = 184;
    private const long OffsetBitMax = 1864;
    private const long UnixEpochTicksDiv1000 = 621_355_968_000_000L;
    private const long UtcOffsetEncodedBits = 1024L;

    /// <summary>
    /// Every DateTimeOffset column that's ever been hit by raw-SQL writes
    /// or downstream-of-bad-data reads. Shared by the service and the
    /// /api/admin/dto-repair-* endpoints so a new failure mode only needs
    /// adding here.
    /// </summary>
    public static readonly (string Table, string Column, bool Nullable)[] Targets =
    {
        ("CatalogTitles",         "FirstSeenAt",     false),
        ("CatalogTitles",         "LastSeenAt",      false),
        ("CatalogLinks",          "CreatedAt",       false),
        ("CatalogLinks",          "HealthCheckedAt", true),
        ("CatalogTitleMetadata",  "LastEnrichedAt",  true),
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
        _log.LogInformation("DateTimeOffset repair: service scheduled (5s startup delay).");
        try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
        catch (OperationCanceledException) { return; }

        try
        {
            var total = 0L;
            await using var db = await _factory.CreateDbContextAsync(stoppingToken);
            foreach (var t in Targets)
            {
                if (stoppingToken.IsCancellationRequested) return;
                total += await RepairAsync(db, t.Table, t.Column, _log, stoppingToken);
            }
            _log.LogInformation("DateTimeOffset repair: pass complete. Re-encoded/healed {Total} row(s).", total);

            // Verify with a second pass — if anything's still dirty, log a warning so
            // ops can hit /api/admin/dto-repair-run manually.
            await using var db2 = await _factory.CreateDbContextAsync(stoppingToken);
            var leftover = 0L;
            foreach (var t in Targets)
            {
                var (u, b) = await CountAsync(db2, t.Table, t.Column, stoppingToken);
                leftover += u + b;
                if (u != 0 || b != 0)
                    _log.LogWarning("DateTimeOffset repair: {Table}.{Col} STILL has {Unix} unix-ms + {Bad} bad-offset row(s).",
                        t.Table, t.Column, u, b);
            }
            if (leftover == 0)
                _log.LogInformation("DateTimeOffset repair: post-repair scan shows 0 bad rows. Database is clean.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogError(ex, "DateTimeOffset repair crashed.");
        }
    }

    /// <summary>
    /// Re-encodes/heals one column. Returns total rows touched. Used both
    /// by the background pass and the manual /api/admin/dto-repair-run.
    /// Going through <see cref="DatabaseFacade.ExecuteSqlRawAsync"/> keeps
    /// EF in control of the connection + implicit transaction (cleaner
    /// than casting GetDbConnection() to SqliteConnection).
    /// </summary>
    public static async Task<long> RepairAsync(
        HarvesterDbContext db, string table, string col, ILogger log, CancellationToken ct)
    {
        // Pass 1 — re-encode raw Unix-ms (closed-form identity for UTC).
        var ceiling = UnixMsCeiling.ToString(CultureInfo.InvariantCulture);
        var unixSql =
            $"UPDATE {table} " +
            $"SET {col} = ({UnixEpochTicksDiv1000} + {col} * 10) * 2048 + {UtcOffsetEncodedBits} " +
            $"WHERE {col} > 0 AND {col} < {ceiling}";
        db.Database.SetCommandTimeout(TimeSpan.FromMinutes(10));
        var swA = System.Diagnostics.Stopwatch.StartNew();
        var fixedUnix = await db.Database.ExecuteSqlRawAsync(unixSql, ct);
        swA.Stop();

        // Pass 2 — heal already-encoded rows whose low 11 bits decode to
        // an offset outside ±840 minutes. Pinning to UTC (1024) preserves
        // the wall-clock ticks portion.
        var badSql =
            $"UPDATE {table} " +
            $"SET {col} = ({col} & ~2047) | {UtcOffsetEncodedBits} " +
            $"WHERE {col} >= {ceiling} " +
            $"  AND (({col} & 2047) < {OffsetBitMin} OR ({col} & 2047) > {OffsetBitMax})";
        var swB = System.Diagnostics.Stopwatch.StartNew();
        var fixedBad = await db.Database.ExecuteSqlRawAsync(badSql, ct);
        swB.Stop();

        if (fixedUnix > 0 || fixedBad > 0)
            log.LogInformation(
                "DateTimeOffset repair: {Table}.{Col} unix-ms {Unix} in {EA}, bad-offset {Bad} in {EB}.",
                table, col, fixedUnix, swA.Elapsed, fixedBad, swB.Elapsed);
        else
            log.LogDebug("DateTimeOffset repair: {Table}.{Col} clean.", table, col);

        return fixedUnix + fixedBad;
    }

    /// <summary>
    /// Returns (unix-ms-rows, bad-offset-rows) for one column. Shared with
    /// the admin status endpoint. Single SQL scan; cheap on a clean DB,
    /// expensive on a dirty one — we only run it on startup and on
    /// explicit admin calls.
    /// </summary>
    public static async Task<(long UnixMs, long BadBits)> CountAsync(
        HarvesterDbContext db, string table, string col, CancellationToken ct)
    {
        var ceiling = UnixMsCeiling.ToString(CultureInfo.InvariantCulture);
        var sql =
            $"SELECT " +
            $"  SUM(CASE WHEN {col} > 0 AND {col} < {ceiling} THEN 1 ELSE 0 END) AS UnixMsRows, " +
            $"  SUM(CASE WHEN {col} >= {ceiling} " +
            $"           AND (({col} & 2047) < {OffsetBitMin} OR ({col} & 2047) > {OffsetBitMax}) " +
            $"        THEN 1 ELSE 0 END) AS BadOffsetRows " +
            $"FROM {table}";

        db.Database.SetCommandTimeout(TimeSpan.FromMinutes(5));
        // SqlQuery<T> needs a queryable result type with matching prop names.
        var rows = await db.Database.SqlQueryRaw<RepairCountRow>(sql).ToListAsync(ct);
        var r = rows.FirstOrDefault();
        return r is null ? (0L, 0L) : (r.UnixMsRows ?? 0L, r.BadOffsetRows ?? 0L);
    }

    public sealed class RepairCountRow
    {
        public long? UnixMsRows { get; set; }
        public long? BadOffsetRows { get; set; }
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
