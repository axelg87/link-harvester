using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LinkHarvester.Persistence.Maintenance;

/// <summary>
/// Recovers DateTimeOffset columns from two compounding bugs:
///
///   1. <b>Hydracker original bug</b>: the catalog ingestor wrote
///      <c>DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()</c> via raw SQL
///      into columns EF expects in its binary-packed form. EF reads the
///      raw unix-ms back, decodes its low 11 bits as a signed-11-bit
///      offset (~18% of values land outside ±14h), throws
///      <c>ArgumentOutOfRangeException</c>.
///
///   2. <b>The repair bug I introduced and shipped across PRs #20-#26</b>:
///      my <c>EncodeBinary</c> helper packed the offset as
///      <c>(offsetMin + 1024) &amp; 0x7FF</c>. The real EF Core 8 converter
///      packs it as <c>(long)offsetMin &amp; 0x7FF</c> — two's-complement
///      signed-11-bit, no bias. For UTC writes my code stored
///      <c>low11 = 1024</c>; EF decoded that as <c>-1024 min</c>, the same
///      out-of-range failure mode. Every row my repair "healed" — every
///      Hydracker fix AND every legitimate EF-written row that my buggy
///      bad-offset detector swept in — landed with <c>low11 = 1024</c>
///      and now throws on read.
///
/// The recovery exploits one fact: my buggy heal preserved the
/// <i>ticks</i> portion of every value (the high 53 bits). Only the low
/// 11 bits (offset) were corrupted. Clearing those low bits leaves the
/// original date intact with offset = UTC. No date data is lost.
///
/// EF Core 8 reference (verified against
/// dotnet/efcore@v8.0.10/src/EFCore/Storage/ValueConversion/DateTimeOffsetToBinaryConverter.cs):
///
///   encode: <c>((v.Ticks / 1000) &lt;&lt; 11) | ((long)v.Offset.TotalMinutes &amp; 0x7FF)</c>
///   decode: <c>new(new DateTime((v &gt;&gt; 11) * 1000), new TimeSpan(0, (int)((v &lt;&lt; 53) &gt;&gt; 53), 0))</c>
///
/// Legal stored low-11-bit ranges (decode succeeds):
///   <c>[0, 840]</c>    — positive offsets 0…+840 min
///   <c>[1208, 2047]</c> — negative offsets −840…−1 min (two's complement)
///
/// Illegal: <c>[841, 1207]</c> — would decode to an offset outside ±14h.
/// My buggy repair's signature is <c>low11 = 1024</c>, smack in the
/// middle of the illegal band.
/// </summary>
public sealed class DateTimeOffsetRepairService : BackgroundService
{
    // unix-ms values for any plausible date are < 10^13 (year ~2603).
    // Binary-encoded values for any plausible date are >> 10^17.
    // 10^15 cleanly separates them.
    private const long UnixMsCeiling = 1_000_000_000_000_000L;

    // Illegal stored-bit window — decodes to an offset outside ±840 min
    // and trips EF's ArgumentOutOfRangeException.
    private const long IllegalLow = 841;
    private const long IllegalHigh = 1207;

    private const long UnixEpochTicksDiv1000 = 621_355_968_000_000L;

    public static readonly (string Table, string Column, bool Nullable)[] Targets =
    {
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
        ("CatalogTitles",         "FirstSeenAt",        false),
        ("CatalogTitles",         "LastSeenAt",         false),
        ("CatalogLinks",          "CreatedAt",          false),
        ("CatalogLinks",          "HealthCheckedAt",    true),
        ("CatalogTitleMetadata",  "LastEnrichedAt",     true),
        ("CatalogImportRuns",     "StartedAt",          false),
        ("CatalogImportRuns",     "FinishedAt",         true),
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
    /// Synchronous PRE-BOOT pass. Runs before <see cref="SettingsService.LoadAsync"/>
    /// so a corrupted AppSettings row can't crashloop the app.
    /// Surgical: touches ONLY AppSettings, ONLY rows with bad low-11-bit
    /// offsets, ONLY the low 11 bits (high ticks preserved). Fast (single
    /// row table), safe (no other table scanned), idempotent.
    /// </summary>
    public static async Task RunPreBootAsync(
        IDbContextFactory<HarvesterDbContext> factory, ILogger log, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var ill_lo = IllegalLow.ToString(CultureInfo.InvariantCulture);
        var ill_hi = IllegalHigh.ToString(CultureInfo.InvariantCulture);

        // Heal only the offset bits. High bits (ticks) untouched ⇒ original
        // wall-clock date preserved. low11 := 0 ⇒ offset = UTC.
        var sql =
            $"UPDATE AppSettings " +
            $"SET UpdatedAt = " +
            $"      CASE WHEN (UpdatedAt & 2047) BETWEEN {ill_lo} AND {ill_hi} " +
            $"           THEN UpdatedAt & ~2047 " +
            $"           ELSE UpdatedAt END, " +
            $"    SynologyResolvedAt = " +
            $"      CASE WHEN SynologyResolvedAt IS NOT NULL " +
            $"            AND (SynologyResolvedAt & 2047) BETWEEN {ill_lo} AND {ill_hi} " +
            $"           THEN SynologyResolvedAt & ~2047 " +
            $"           ELSE SynologyResolvedAt END";
        var n = await db.Database.ExecuteSqlRawAsync(sql, ct);
        log.LogInformation("DateTimeOffset pre-boot: AppSettings touched {N} row(s) (surgical offset-bit heal).", n);
    }

    /// <summary>
    /// Per-column counts of rows in each known-bad state. Single scan.
    /// Pre-flight before any UPDATE so an operator can verify the model
    /// matches reality.
    /// </summary>
    public static async Task<RepairCounts> CountAsync(
        HarvesterDbContext db, string table, string col, CancellationToken ct)
    {
        var ceiling = UnixMsCeiling.ToString(CultureInfo.InvariantCulture);
        var sql =
            $"SELECT " +
            $"  SUM(CASE WHEN {col} > 0 AND {col} < {ceiling} THEN 1 ELSE 0 END) AS UnixMsRows, " +
            $"  SUM(CASE WHEN {col} >= {ceiling} " +
            $"           AND ({col} & 2047) BETWEEN {IllegalLow} AND {IllegalHigh} " +
            $"        THEN 1 ELSE 0 END) AS BadOffsetRows " +
            $"FROM {table}";
        db.Database.SetCommandTimeout(TimeSpan.FromMinutes(5));
        var rows = await db.Database.SqlQueryRaw<RepairCountRow>(sql).ToListAsync(ct);
        var r = rows.FirstOrDefault();
        return r is null
            ? new RepairCounts(0, 0)
            : new RepairCounts(r.UnixMsRows ?? 0L, r.BadOffsetRows ?? 0L);
    }

    /// <summary>
    /// Full per-column repair. Two UPDATE passes:
    ///   1. Unix-ms values (col &lt; 10^15) → re-encode using the actual
    ///      EF Core 8 formula. No <c>+1024</c>.
    ///   2. Bad-offset values (col ≥ 10^15, low11 ∈ [841, 1207]) → clear
    ///      the low 11 bits. Ticks portion preserved, offset becomes UTC.
    /// </summary>
    public static async Task<long> RepairAsync(
        HarvesterDbContext db, string table, string col, ILogger log, CancellationToken ct)
    {
        var ceiling = UnixMsCeiling.ToString(CultureInfo.InvariantCulture);
        db.Database.SetCommandTimeout(TimeSpan.FromMinutes(10));

        // Pass 1: re-encode raw unix-ms with the correct formula. low11 = 0 (UTC).
        var unixSql =
            $"UPDATE {table} " +
            $"SET {col} = ({UnixEpochTicksDiv1000} + {col} * 10) * 2048 " +
            $"WHERE {col} > 0 AND {col} < {ceiling}";
        var swA = System.Diagnostics.Stopwatch.StartNew();
        var fixedUnix = await db.Database.ExecuteSqlRawAsync(unixSql, ct);
        swA.Stop();

        // Pass 2: heal rows with illegal offset bits (the [841, 1207]
        // band, including 1024 which is my repair's own signature).
        // Clearing the low 11 bits leaves the ticks portion intact and
        // sets offset to UTC.
        var badSql =
            $"UPDATE {table} " +
            $"SET {col} = {col} & ~2047 " +
            $"WHERE {col} >= {ceiling} " +
            $"  AND ({col} & 2047) BETWEEN {IllegalLow} AND {IllegalHigh}";
        var swB = System.Diagnostics.Stopwatch.StartNew();
        var fixedBad = await db.Database.ExecuteSqlRawAsync(badSql, ct);
        swB.Stop();

        if (fixedUnix > 0 || fixedBad > 0)
            log.LogInformation(
                "DateTimeOffset repair: {Table}.{Col} unix-ms {Unix} in {EA}, bad-offset {Bad} in {EB}.",
                table, col, fixedUnix, swA.Elapsed, fixedBad, swB.Elapsed);
        return fixedUnix + fixedBad;
    }

    public sealed record RepairCounts(long UnixMs, long BadBits)
    {
        public long Total => UnixMs + BadBits;
    }

    public sealed class RepairCountRow
    {
        public long? UnixMsRows { get; set; }
        public long? BadOffsetRows { get; set; }
    }

    /// <summary>
    /// Matches EF Core 8's <c>DateTimeOffsetToBinaryConverter</c> exactly.
    /// Verified against EF Core source AND covered by a unit test that
    /// invokes the actual converter — see <c>DateTimeOffsetRepairTests</c>.
    /// </summary>
    public static long EncodeBinary(DateTimeOffset dto)
    {
        var offsetMin = (long)dto.Offset.TotalMinutes;
        return ((dto.Ticks / 1000) << 11) | (offsetMin & 0x7FF);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("DateTimeOffset repair: BackgroundService scheduled (5s delay).");
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
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.LogError(ex, "DateTimeOffset repair crashed."); }
    }
}
