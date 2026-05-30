using System.Text.Json;
using LinkHarvester.Core;
using LinkHarvester.Persistence.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

// CardKeeper builds SQL with int-id CSV inlined into the IN clause. The ids
// come from typed int columns we just read from EF — never from user input
// — so the EF1002 "interpolated SQL" warning is a false positive here.
#pragma warning disable EF1002

namespace LinkHarvester.Persistence.Cards;

/// <summary>
/// Default <see cref="ICardKeeper"/> implementation. Single-row upserts read
/// the current base state via EF queries, then write the card row via raw
/// SQL <c>INSERT … ON CONFLICT DO UPDATE</c>. Bulk paths run a single
/// <c>INSERT … SELECT</c> against base, suitable for the catalog ingester
/// and the startup backfill.
///
/// The keeper holds no state; it can be a singleton.
/// </summary>
public sealed class CardKeeper : ICardKeeper
{
    private readonly ILogger<CardKeeper> _log;

    public CardKeeper(ILogger<CardKeeper>? log = null)
    {
        _log = log ?? NullLogger<CardKeeper>.Instance;
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    // ─── Inbox ───────────────────────────────────────────────────────────────

    public Task UpsertInboxCardAsync(HarvesterDbContext db, int titleId, CancellationToken ct)
        => UpsertInboxCardsAsync(db, new[] { titleId }, ct);

    public async Task UpsertInboxCardsAsync(HarvesterDbContext db, IReadOnlyCollection<int> titleIds, CancellationToken ct)
    {
        if (titleIds.Count == 0) return;

        // Pull the base data needed to build each card. The previous reader
        // path loaded the entire Articles collection per title and filtered
        // in memory; we fetch only the visible-status, last-3-days slice via
        // a single query with AsSplitQuery so titles and articles each come
        // back without cartesian join expansion.
        var cutoff = DateTimeOffset.UtcNow.AddDays(-3);
        var titles = await db.Titles.AsNoTracking()
            .Where(t => titleIds.Contains(t.Id))
            .ToListAsync(ct);
        if (titles.Count == 0)
        {
            await RemoveCardsByTitleIdsAsync(db, titleIds, ct);
            return;
        }

        var articles = await db.Articles.AsNoTracking()
            .Where(a => titleIds.Contains(a.TitleId)
                && a.DiscoveredAt >= cutoff
                && (a.Status == ArticleStatus.Parsed
                    || a.Status == ArticleStatus.Resolved
                    || a.Status == ArticleStatus.InInbox))
            .OrderByDescending(a => a.QualityScore)
            .ToListAsync(ct);
        var articlesByTitle = articles.GroupBy(a => a.TitleId).ToDictionary(g => g.Key, g => g.ToList());

        var catalogIds = titles.Where(t => t.CatalogTitleId.HasValue).Select(t => t.CatalogTitleId!.Value).Distinct().ToList();
        var catalogById = catalogIds.Count == 0
            ? new Dictionary<int, CatalogTitleEntity>()
            : await db.CatalogTitles.AsNoTracking()
                .Include(t => t.Metadata)
                .Where(t => catalogIds.Contains(t.Id))
                .ToDictionaryAsync(t => t.Id, ct);

        foreach (var t in titles)
        {
            var visible = articlesByTitle.TryGetValue(t.Id, out var list) ? list : new List<ArticleEntity>();
            if (visible.Count == 0
                || (t.Status != TitleStatus.InInbox && t.Status != TitleStatus.New))
            {
                // Title left the inbox (Submitted/Skipped) or has no visible
                // articles within the cutoff. Card becomes invisible — kept
                // for "un-submit" / "un-skip" round-trips without rebuild.
                await UpsertInboxCardRowAsync(db, BuildEmptyInboxCard(t), ct);
                continue;
            }

            var chosen = visible[0];
            var others = visible.Skip(1).ToList();
            CatalogTitleEntity? catalog = null;
            CatalogTitleMetadataEntity? meta = null;
            if (t.CatalogTitleId is int cid && catalogById.TryGetValue(cid, out var ct2))
            {
                catalog = ct2;
                meta = ct2.Metadata;
            }

            var card = new InboxCardEntity
            {
                TitleId = t.Id,
                CatalogTitleId = t.CatalogTitleId,
                DisplayTitle = t.DisplayTitle,
                Poster = catalog?.TitlePoster,
                Year = meta?.Year ?? t.Year,
                Rating = meta?.VoteAverage,
                GenresJson = meta?.GenresJson ?? "[]",
                MetadataUncertain = meta?.MetadataUncertain == true,
                Kind = t.Kind.ToString(),
                SeasonNumber = t.SeasonNumber,
                BetterAvailable = t.Status == TitleStatus.Submitted
                    && t.BestSubmittedQualityScore is int s
                    && chosen.QualityScore > s,
                UpdatedAt = t.UpdatedAt,
                ChosenArticleJson = JsonSerializer.Serialize(ToArticleDto(chosen), Json),
                OtherVariantsJson = JsonSerializer.Serialize(others.Select(ToArticleDto), Json),
                Visible = true,
            };
            await UpsertInboxCardRowAsync(db, card, ct);
        }
    }

    public async Task RemoveInboxCardAsync(HarvesterDbContext db, int titleId, CancellationToken ct)
    {
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE InboxCards SET Visible = 0 WHERE TitleId = {0}",
            new object[] { titleId }, ct);
    }

    private static async Task RemoveCardsByTitleIdsAsync(HarvesterDbContext db, IReadOnlyCollection<int> titleIds, CancellationToken ct)
    {
        var sql = "DELETE FROM InboxCards WHERE TitleId IN (" + string.Join(',', titleIds) + ")";
        await db.Database.ExecuteSqlRawAsync(sql, ct);
    }

    private static InboxCardEntity BuildEmptyInboxCard(TitleEntity t) => new()
    {
        TitleId = t.Id,
        CatalogTitleId = t.CatalogTitleId,
        DisplayTitle = t.DisplayTitle,
        Kind = t.Kind.ToString(),
        SeasonNumber = t.SeasonNumber,
        UpdatedAt = t.UpdatedAt,
        Visible = false,
    };

    private static async Task UpsertInboxCardRowAsync(HarvesterDbContext db, InboxCardEntity c, CancellationToken ct)
    {
        // SQLite ON CONFLICT upsert. The conversion DateTimeOffset → long is
        // handled by HarvesterDbContext's ConfigureConventions; here we
        // bypass that path so we encode UpdatedAt ourselves.
        await db.Database.ExecuteSqlRawAsync(@"
INSERT INTO InboxCards
    (TitleId, CatalogTitleId, DisplayTitle, Poster, Year, Rating, GenresJson,
     MetadataUncertain, Kind, SeasonNumber, BetterAvailable, UpdatedAt,
     ChosenArticleJson, OtherVariantsJson, Visible)
VALUES ({0},{1},{2},{3},{4},{5},{6},{7},{8},{9},{10},{11},{12},{13},{14})
ON CONFLICT(TitleId) DO UPDATE SET
    CatalogTitleId      = excluded.CatalogTitleId,
    DisplayTitle        = excluded.DisplayTitle,
    Poster              = excluded.Poster,
    Year                = excluded.Year,
    Rating              = excluded.Rating,
    GenresJson          = excluded.GenresJson,
    MetadataUncertain   = excluded.MetadataUncertain,
    Kind                = excluded.Kind,
    SeasonNumber        = excluded.SeasonNumber,
    BetterAvailable     = excluded.BetterAvailable,
    UpdatedAt           = excluded.UpdatedAt,
    ChosenArticleJson   = excluded.ChosenArticleJson,
    OtherVariantsJson   = excluded.OtherVariantsJson,
    Visible             = excluded.Visible;",
            new object?[]
            {
                c.TitleId,
                c.CatalogTitleId,
                c.DisplayTitle,
                c.Poster,
                c.Year,
                c.Rating,
                c.GenresJson,
                c.MetadataUncertain ? 1 : 0,
                c.Kind,
                c.SeasonNumber,
                c.BetterAvailable ? 1 : 0,
                EncodeDateTimeOffsetBinary(c.UpdatedAt),
                c.ChosenArticleJson,
                c.OtherVariantsJson,
                c.Visible ? 1 : 0,
            }!, ct);
    }

    // Mirrors EFCore's DateTimeOffsetToBinaryConverter (see EFCore source).
    // Required because we INSERT raw values via ExecuteSqlRawAsync, which
    // bypasses the value-converter wired up in OnModelCreating. Writing the
    // Unix-ms representation here instead produced low-11-bit garbage that
    // EF's reader decoded as an offset outside +/-14h, throwing on every
    // InboxCards row.
    private static long EncodeDateTimeOffsetBinary(DateTimeOffset v) =>
        (v.Ticks / 1000) << 11 | ((long)v.Offset.TotalMinutes & 0x7FF);

    private static InboxArticleDto ToArticleDto(ArticleEntity a) => new(
        Id: a.Id,
        Url: a.Url,
        QualityLabel: a.QualityLabel ?? "?",
        QualityScore: a.QualityScore,
        Resolution: a.Resolution,
        Codec: a.Codec,
        SourceTier: a.SourceTier,
        Language: a.Language,
        SizeBytes: a.SizeBytes,
        EpisodeCount: a.EpisodeCount,
        Hosters: ParseHosters(a.HostersJson),
        Status: a.Status.ToString());

    private static List<HosterSummaryDto> ParseHosters(string json)
    {
        try
        {
            var hl = JsonSerializer.Deserialize<List<HosterLinks>>(json) ?? new();
            return hl.Select(h => new HosterSummaryDto(h.Hoster, h.Links.Count)).ToList();
        }
        catch { return new(); }
    }

    // Embedded DTO shapes — kept private to avoid coupling Persistence to
    // the API project. The reader endpoint deserialises the same JSON shape
    // and the wire contract lives in InboxEndpoints.cs.
    private sealed record InboxArticleDto(
        int Id, string Url, string QualityLabel, int QualityScore,
        string? Resolution, string? Codec, string? SourceTier, string? Language,
        long? SizeBytes, int? EpisodeCount,
        List<HosterSummaryDto> Hosters, string Status);
    private sealed record HosterSummaryDto(string Hoster, int LinkCount);

    // ─── Catalog ─────────────────────────────────────────────────────────────

    public Task UpsertCatalogCardAsync(HarvesterDbContext db, int catalogTitleId, CancellationToken ct)
        => UpsertCatalogCardsAsync(db, new[] { catalogTitleId }, ct);

    public async Task UpsertCatalogCardsAsync(HarvesterDbContext db, IReadOnlyCollection<int> catalogTitleIds, CancellationToken ct)
    {
        if (catalogTitleIds.Count == 0) return;
        var idsCsv = string.Join(',', catalogTitleIds);

        // Three SQL passes per batch: card scalars, genre rows, link-facet
        // rows. Each pass projects directly from base into card tables
        // with no client-side roundtrip — for the ingester this is the only
        // way to keep cards in sync without crippling import throughput.
        await db.Database.ExecuteSqlRawAsync($@"
INSERT INTO CatalogCards
    (TitleId, TitleName, OriginalTitle, NormalizedTitle, CategoryName, TitlePoster,
     LinkCount, EpisodeCount, Year, Rating, Runtime, Overview, GenresJson,
     OriginalLanguage, EnrichmentSource, MetadataUncertain, HasMetadata,
     SeasonMin, SeasonMax, Popularity, IsHidden)
SELECT
    ct.Id, ct.TitleName, ct.OriginalTitle, ct.NormalizedTitle, ct.CategoryName, ct.TitlePoster,
    ct.LinkCount, ct.EpisodeCount,
    m.Year, m.VoteAverage, m.Runtime, m.Overview, COALESCE(m.GenresJson, '[]'),
    m.OriginalLanguage, m.EnrichmentSource,
    COALESCE(m.MetadataUncertain, 0),
    CASE WHEN m.EnrichmentSource LIKE 'tmdb_%' THEN 1 ELSE 0 END,
    (SELECT MIN(SeasonNumber) FROM CatalogEpisodes WHERE TitleId = ct.Id AND SeasonNumber > 0),
    (SELECT MAX(SeasonNumber) FROM CatalogEpisodes WHERE TitleId = ct.Id AND SeasonNumber > 0),
    m.Popularity,
    CASE WHEN ct.IsHidden THEN 1 ELSE 0 END
FROM CatalogTitles ct
LEFT JOIN CatalogTitleMetadata m ON m.TitleId = ct.Id
WHERE ct.Id IN ({idsCsv})
ON CONFLICT(TitleId) DO UPDATE SET
    TitleName         = excluded.TitleName,
    OriginalTitle     = excluded.OriginalTitle,
    NormalizedTitle   = excluded.NormalizedTitle,
    CategoryName      = excluded.CategoryName,
    TitlePoster       = excluded.TitlePoster,
    LinkCount         = excluded.LinkCount,
    EpisodeCount      = excluded.EpisodeCount,
    Year              = excluded.Year,
    Rating            = excluded.Rating,
    Runtime           = excluded.Runtime,
    Overview          = excluded.Overview,
    GenresJson        = excluded.GenresJson,
    OriginalLanguage  = excluded.OriginalLanguage,
    EnrichmentSource  = excluded.EnrichmentSource,
    MetadataUncertain = excluded.MetadataUncertain,
    HasMetadata       = excluded.HasMetadata,
    SeasonMin         = excluded.SeasonMin,
    SeasonMax         = excluded.SeasonMax,
    Popularity        = excluded.Popularity,
    IsHidden          = excluded.IsHidden;", ct);

        // Refresh genre join rows: wipe + reinsert is simpler than diffing
        // and the cardinality is tiny (avg ~3 genres per title). DISTINCT
        // because TMDB occasionally serves duplicate genres for the same
        // title (observed in the wild: ["Drama","Drama"]) — without it the
        // UNIQUE constraint on (CardId, Genre) fires mid-rebuild.
        await db.Database.ExecuteSqlRawAsync($"DELETE FROM CatalogCardGenres WHERE CardId IN ({idsCsv})", ct);
        await db.Database.ExecuteSqlRawAsync($@"
INSERT INTO CatalogCardGenres (CardId, Genre)
SELECT DISTINCT m.TitleId, TRIM(je.value)
FROM CatalogTitleMetadata m, json_each(m.GenresJson) je
WHERE m.TitleId IN ({idsCsv})
  AND json_valid(m.GenresJson)
  AND je.value IS NOT NULL
  AND LENGTH(TRIM(je.value)) > 0;", ct);

        // Refresh link-facet rows. One row per CatalogLink (no de-dup) so the
        // filter EXISTS subquery has perfectly composable predicates.
        await db.Database.ExecuteSqlRawAsync($"DELETE FROM CatalogCardLinkFacets WHERE CardId IN ({idsCsv})", ct);
        await db.Database.ExecuteSqlRawAsync($@"
INSERT INTO CatalogCardLinkFacets (CardId, NormalizedHost, QualityName, AudioLangs, SizeBytes)
SELECT l.TitleId, l.NormalizedHost, l.QualityName, l.AudioLangs, l.SizeBytes
FROM CatalogLinks l
WHERE l.TitleId IN ({idsCsv});", ct);
    }

    public async Task RemoveCatalogCardAsync(HarvesterDbContext db, int catalogTitleId, CancellationToken ct)
    {
        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM CatalogCards WHERE TitleId = {0}",
            new object[] { catalogTitleId }, ct);
        // FK isn't enforced — cascade manually.
        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM CatalogCardGenres WHERE CardId = {0}",
            new object[] { catalogTitleId }, ct);
        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM CatalogCardLinkFacets WHERE CardId = {0}",
            new object[] { catalogTitleId }, ct);
    }

    // ─── Reconcile ───────────────────────────────────────────────────────────

    public async Task RebuildAllAsync(HarvesterDbContext db, CancellationToken ct)
    {
        var totalSw = System.Diagnostics.Stopwatch.StartNew();

        // ── Inbox cards ──────────────────────────────────────────────────
        _log.LogInformation("rebuild: inbox phase starting");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM InboxCards", ct);
        var inboxIds = await db.Titles.AsNoTracking()
            .Select(t => t.Id)
            .ToListAsync(ct);
        _log.LogInformation("rebuild: inbox {Count} titles to upsert", inboxIds.Count);

        const int batch = 500;
        var inboxSw = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < inboxIds.Count; i += batch)
        {
            var slice = inboxIds.Skip(i).Take(batch).ToList();
            // Wrap each batch in an explicit transaction. Without this, the
            // ~500 per-row UpsertInboxCardRowAsync calls inside
            // UpsertInboxCardsAsync each auto-commit, which under WAL means
            // ~500 fsync barriers per batch. With one transaction per batch
            // the barriers collapse to ~1 per batch — ~100× speedup on the
            // dominant inbox path.
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await UpsertInboxCardsAsync(db, slice, ct);
            await tx.CommitAsync(ct);

            if ((i / batch) % 10 == 0 || i + batch >= inboxIds.Count)
            {
                _log.LogInformation("rebuild: inbox {Done}/{Total} elapsed {Elapsed}",
                    Math.Min(i + batch, inboxIds.Count), inboxIds.Count, inboxSw.Elapsed);
            }
        }
        _log.LogInformation("rebuild: inbox phase done in {Elapsed}", inboxSw.Elapsed);

        // ── Catalog cards ────────────────────────────────────────────────
        _log.LogInformation("rebuild: catalog phase starting");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM CatalogCards", ct);
        await db.Database.ExecuteSqlRawAsync("DELETE FROM CatalogCardGenres", ct);
        await db.Database.ExecuteSqlRawAsync("DELETE FROM CatalogCardLinkFacets", ct);
        var catalogIds = await db.CatalogTitles.AsNoTracking()
            .Select(t => t.Id)
            .ToListAsync(ct);
        _log.LogInformation("rebuild: catalog {Count} titles to upsert", catalogIds.Count);

        const int catalogBatch = 2000;
        var catalogSw = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < catalogIds.Count; i += catalogBatch)
        {
            var slice = catalogIds.Skip(i).Take(catalogBatch).ToList();
            // UpsertCatalogCardsAsync is already bulk INSERT…SELECT (5
            // statements per batch), so a per-batch transaction collapses
            // the 5 commits into 1.
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await UpsertCatalogCardsAsync(db, slice, ct);
            await tx.CommitAsync(ct);

            _log.LogInformation("rebuild: catalog {Done}/{Total} elapsed {Elapsed}",
                Math.Min(i + catalogBatch, catalogIds.Count), catalogIds.Count, catalogSw.Elapsed);
        }
        _log.LogInformation("rebuild: catalog phase done in {Elapsed}", catalogSw.Elapsed);
        _log.LogInformation("rebuild: total elapsed {Elapsed}", totalSw.Elapsed);
    }
}
