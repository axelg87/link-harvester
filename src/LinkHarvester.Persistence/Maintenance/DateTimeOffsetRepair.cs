using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LinkHarvester.Persistence.Maintenance;

/// <summary>
/// One-shot repair for DateTimeOffset columns whose long-encoded value
/// can't round-trip through EF Core's <c>DateTimeOffsetToBinaryConverter</c>
/// (low 11 bits decode to an offset outside ±840 minutes, throwing
/// <c>ArgumentOutOfRangeException</c>). EF reads materialise every column
/// on an entity, so one bad row in one column 500s the whole endpoint.
///
/// Three failure modes per column, all handled here:
///
///  1. Raw Unix-ms (Hydracker ingestor's original bug — wrote
///     <c>ToUnixTimeMilliseconds()</c> via raw SQL into a column EF
///     expects in binary-encoded form).
///
///  2. Stored value of <c>0</c>. SQLite default for an INTEGER column
///     when a NOT NULL column was added via migration with no DEFAULT —
///     existing rows backfill to 0. EF reads 0 as
///     <c>offset = (0 &amp; 0x7FF) - 1024 = -1024 minutes</c> → throw.
///     Fix: set to <c>1024</c> (epoch UTC) for non-nullable columns, or
///     to NULL for nullable columns (where 0 was likely meant to be
///     "not set").
///
///  3. Already-encoded form (≥ 10^15) but with low 11 bits decoding
///     outside ±840 minutes. Heal preserves wall-clock ticks and pins
///     offset to UTC: <c>(col &amp; ~2047) | 1024</c>.
///
/// Targets cover every <c>DateTimeOffset</c> / <c>DateTimeOffset?</c>
/// column EF materialises across the inbox + catalog + harvester reads.
/// </summary>
public sealed class DateTimeOffsetRepairService : BackgroundService
{
    private const long UnixMsCeiling = 1_000_000_000_000_000L;
    private const long OffsetBitMin = 184;
    private const long OffsetBitMax = 1864;
    private const long UnixEpochTicksDiv1000 = 621_355_968_000_000L;
    private const long UtcOffsetEncodedBits = 1024L;

    /// <summary>
    /// Every DateTimeOffset / DateTimeOffset? column in the schema. Adding
    /// a new persisted DateTimeOffset → add it here so EF reads can't
    /// trip on it.
    /// </summary>
    public static readonly (string Table, string Column, bool Nullable)[] Targets =
    {
        // Harvester side — read by /api/inbox and the submission flow.
        ("Titles",                "CreatedAt",         false),
        ("Titles",                "UpdatedAt",         false),
        ("Titles",                "CatalogPromotedAt", true),
        ("Articles",              "DiscoveredAt",      false),
        ("Articles",              "ResolvedAt",        true),
        ("Articles",              "SubmittedAt",       true),
        ("ResolvedLinks",         "ResolvedAt",        false),
        ("Submissions",           "SubmittedAt",       false),
        ("Submissions",           "CompletedAt",       true),
        ("ScanRuns",              "StartedAt",         false),
        ("ScanRuns",              "FinishedAt",        true),
        ("BackfillRuns",          "FromDate",          false),
        ("BackfillRuns",          "StartedAt",         false),
        ("BackfillRuns",          "FinishedAt",        true),
        ("HealthSweepRuns",       "StartedAt",         false),
        ("HealthSweepRuns",       "FinishedAt",        true),
        ("AppSettings",           "SynologyResolvedAt", true),
        ("AppSettings",           "UpdatedAt",          false),
        // Catalog side — read by /api/catalog/* and /api/inbox via Metadata join.
        ("CatalogTitles",         "FirstSeenAt",       false),
        ("CatalogTitles",         "LastSeenAt",        false),
        ("CatalogLinks",          "CreatedAt",         false),
        ("CatalogLinks",          "HealthCheckedAt",   true),
        ("CatalogTitleMetadata",  "LastEnrichedAt",    true),
        ("CatalogImportRuns",     "StartedAt",         false),
        ("CatalogImportRuns",     "FinishedAt",        true),
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

    /// <summary>
    /// Synchronous PRE-BOOT pass. Runs from <c>Program.cs</c> before any
    /// EF read (especially <see cref="SettingsService.LoadAsync"/>, which
    /// is the first EF read at startup and the most common crashloop
    /// trigger).
    ///
    /// Two responsibilities:
    ///
    /// <list type="number">
    ///   <item><description>
    ///     <b>Force-reset <c>AppSettings</c></b> DateTimeOffset columns.
    ///     The table has exactly one row; we log its current raw long
    ///     values for forensics, then unconditionally rewrite the row's
    ///     <c>UpdatedAt</c> to a known-good encoded UtcNow. Diagnostic
    ///     status counts have repeatedly reported "0 bad" while EF still
    ///     throws on materialise, so we stop trusting WHERE clauses and
    ///     just nuke the values. Cost: the <c>UpdatedAt</c> timestamp
    ///     resets and any <c>SynologyResolvedAt</c> with bad bits gets
    ///     nulled (QuickConnect will re-resolve). Both are recoverable.
    ///   </description></item>
    ///   <item><description>
    ///     <b>Three-pass repair on small tables only</b>. The full pass
    ///     on <c>CatalogLinks</c> (2.3M rows) takes ~3 minutes — blows
    ///     Fly's 60s health-check grace and trips deploys. Small tables
    ///     finish in seconds; <c>CatalogLinks</c> is left to the
    ///     <see cref="BackgroundService"/> after Kestrel starts.
    ///   </description></item>
    /// </list>
    /// </summary>
    public static async Task RunPreBootAsync(
        IDbContextFactory<HarvesterDbContext> factory, ILogger log, CancellationToken ct)
    {
        log.LogInformation("DateTimeOffset pre-boot: starting.");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await using var db = await factory.CreateDbContextAsync(ct);

        await ForceResetAppSettingsAsync(db, log, ct);

        // Run the 3-pass repair on every Target EXCEPT the giant CatalogLinks
        // table. AppSettings columns are handled above (force-reset). All
        // other tables are small enough to finish in seconds.
        foreach (var t in Targets)
        {
            if (t.Table == "CatalogLinks") continue;          // deferred to BackgroundService
            if (t.Table == "AppSettings") continue;           // already force-reset
            await RepairAsync(db, t.Table, t.Column, t.Nullable, log, ct);
        }

        sw.Stop();
        log.LogInformation("DateTimeOffset pre-boot: done in {Elapsed}. CatalogLinks deferred to BackgroundService.", sw.Elapsed);
    }

    /// <summary>
    /// Single-row table — force its DateTimeOffset columns to known-good
    /// encodings regardless of current state. Used when WHERE-based
    /// detection has failed and we just need the app to boot.
    /// </summary>
    private static async Task ForceResetAppSettingsAsync(
        HarvesterDbContext db, ILogger log, CancellationToken ct)
    {
        // Forensics: log what we're about to overwrite. If the value really
        // does decode to a bad offset, this is the first time we'll have
        // ground truth on what's stored.
        try
        {
            var rows = await db.Database
                .SqlQueryRaw<AppSettingsRawRow>(
                    "SELECT Id AS Id, UpdatedAt AS UpdatedAt, SynologyResolvedAt AS SynologyResolvedAt FROM AppSettings")
                .ToListAsync(ct);
            foreach (var r in rows)
            {
                log.LogInformation(
                    "DateTimeOffset pre-boot: AppSettings row Id={Id} UpdatedAt={UpdatedAt} (low11={Low}), SynologyResolvedAt={Resolved}.",
                    r.Id, r.UpdatedAt, r.UpdatedAt & 2047, r.SynologyResolvedAt?.ToString() ?? "NULL");
            }
        }
        catch (Exception ex) { log.LogWarning(ex, "DateTimeOffset pre-boot: could not log AppSettings raw values."); }

        var nowEncoded = EncodeBinary(DateTimeOffset.UtcNow);
        var sql =
            $"UPDATE AppSettings " +
            $"SET UpdatedAt = {nowEncoded}, " +
            $"    SynologyResolvedAt = CASE " +
            $"      WHEN SynologyResolvedAt IS NULL THEN NULL " +
            $"      WHEN SynologyResolvedAt >= 1000000000000000 " +
            $"           AND ((SynologyResolvedAt & 2047) BETWEEN 184 AND 1864) " +
            $"      THEN SynologyResolvedAt " +
            $"      ELSE NULL END";
        var n = await db.Database.ExecuteSqlRawAsync(sql, ct);
        log.LogInformation("DateTimeOffset pre-boot: AppSettings UpdatedAt force-reset to {Encoded}; {N} row(s).", nowEncoded, n);
    }

    public sealed class AppSettingsRawRow
    {
        public int Id { get; set; }
        public long UpdatedAt { get; set; }
        public long? SynologyResolvedAt { get; set; }
    }

    /// <summary>
    /// Legacy entry point — kept for callers that want the full pass
    /// across every column (e.g. an admin endpoint). Slow on CatalogLinks.
    /// </summary>
    public static async Task RunAllAsync(
        IDbContextFactory<HarvesterDbContext> factory, ILogger log, CancellationToken ct)
    {
        log.LogInformation("DateTimeOffset repair: starting full pass across {N} column(s).", Targets.Length);
        var total = 0L;
        await using var db = await factory.CreateDbContextAsync(ct);
        foreach (var t in Targets)
        {
            total += await RepairAsync(db, t.Table, t.Column, t.Nullable, log, ct);
        }
        log.LogInformation("DateTimeOffset repair: full pass done. Re-encoded/healed {Total} row(s).", total);
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
                total += await RepairAsync(db, t.Table, t.Column, t.Nullable, _log, stoppingToken);
            }
            _log.LogInformation("DateTimeOffset repair: pass complete. Re-encoded/healed {Total} row(s).", total);

            await using var db2 = await _factory.CreateDbContextAsync(stoppingToken);
            var leftover = 0L;
            foreach (var t in Targets)
            {
                var c = await CountAsync(db2, t.Table, t.Column, stoppingToken);
                leftover += c.Total;
                if (c.Total != 0)
                    _log.LogWarning("DateTimeOffset repair: {Table}.{Col} STILL has {Unix} unix-ms + {Zero} zero + {Bad} bad-offset.",
                        t.Table, t.Column, c.UnixMs, c.Zero, c.BadBits);
            }
            if (leftover == 0)
                _log.LogInformation("DateTimeOffset repair: post-repair scan shows 0 bad rows across {N} column(s). Database clean.", Targets.Length);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogError(ex, "DateTimeOffset repair crashed.");
        }
    }

    /// <summary>
    /// Re-encode and heal one column. Returns total rows touched.
    /// Three passes (unix-ms, zero, bad-offset) each in its own implicit
    /// transaction so a slow pass doesn't lock the table indefinitely.
    /// </summary>
    public static async Task<long> RepairAsync(
        HarvesterDbContext db, string table, string col, bool nullable, ILogger log, CancellationToken ct)
    {
        var ceiling = UnixMsCeiling.ToString(CultureInfo.InvariantCulture);
        db.Database.SetCommandTimeout(TimeSpan.FromMinutes(10));

        // 1) Unix-ms → binary-encoded UTC.
        var unixSql =
            $"UPDATE {table} " +
            $"SET {col} = ({UnixEpochTicksDiv1000} + {col} * 10) * 2048 + {UtcOffsetEncodedBits} " +
            $"WHERE {col} > 0 AND {col} < {ceiling}";
        var fixedUnix = await db.Database.ExecuteSqlRawAsync(unixSql, ct);

        // 2) Stored zero. Nullable column → NULL is the semantically right
        //    "not set"; non-nullable → 1024 (encoded epoch UTC) so EF can
        //    read it without throwing.
        var zeroTarget = nullable ? "NULL" : UtcOffsetEncodedBits.ToString(CultureInfo.InvariantCulture);
        var zeroSql =
            $"UPDATE {table} SET {col} = {zeroTarget} WHERE {col} = 0";
        var fixedZero = await db.Database.ExecuteSqlRawAsync(zeroSql, ct);

        // 3) Already-encoded with bad offset bits → pin offset to UTC.
        var badSql =
            $"UPDATE {table} " +
            $"SET {col} = ({col} & ~2047) | {UtcOffsetEncodedBits} " +
            $"WHERE {col} >= {ceiling} " +
            $"  AND (({col} & 2047) < {OffsetBitMin} OR ({col} & 2047) > {OffsetBitMax})";
        var fixedBad = await db.Database.ExecuteSqlRawAsync(badSql, ct);

        var total = fixedUnix + fixedZero + fixedBad;
        if (total > 0)
            log.LogInformation(
                "DateTimeOffset repair: {Table}.{Col} unix-ms {Unix}, zero {Zero}, bad-offset {Bad}.",
                table, col, fixedUnix, fixedZero, fixedBad);
        return total;
    }

    /// <summary>
    /// Counts per failure mode for one column. Single scan over the table.
    /// </summary>
    public static async Task<RepairCounts> CountAsync(
        HarvesterDbContext db, string table, string col, CancellationToken ct)
    {
        var ceiling = UnixMsCeiling.ToString(CultureInfo.InvariantCulture);
        var sql =
            $"SELECT " +
            $"  SUM(CASE WHEN {col} > 0 AND {col} < {ceiling} THEN 1 ELSE 0 END) AS UnixMsRows, " +
            $"  SUM(CASE WHEN {col} = 0 THEN 1 ELSE 0 END) AS ZeroRows, " +
            $"  SUM(CASE WHEN {col} >= {ceiling} " +
            $"           AND (({col} & 2047) < {OffsetBitMin} OR ({col} & 2047) > {OffsetBitMax}) " +
            $"        THEN 1 ELSE 0 END) AS BadOffsetRows " +
            $"FROM {table}";

        db.Database.SetCommandTimeout(TimeSpan.FromMinutes(5));
        var rows = await db.Database.SqlQueryRaw<RepairCountRow>(sql).ToListAsync(ct);
        var r = rows.FirstOrDefault();
        return r is null
            ? new RepairCounts(0, 0, 0)
            : new RepairCounts(r.UnixMsRows ?? 0L, r.ZeroRows ?? 0L, r.BadOffsetRows ?? 0L);
    }

    public sealed record RepairCounts(long UnixMs, long Zero, long BadBits)
    {
        public long Total => UnixMs + Zero + BadBits;
    }

    public sealed class RepairCountRow
    {
        public long? UnixMsRows { get; set; }
        public long? ZeroRows { get; set; }
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
