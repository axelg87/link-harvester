using System.Text.Json;
using LinkHarvester.Core;
using LinkHarvester.Persistence;
using LinkHarvester.Persistence.Cards;
using LinkHarvester.Persistence.Catalog;
using LinkHarvester.Worker;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LinkHarvester.Api.Endpoints;

public static class InboxEndpoints
{
    public static IEndpointRouteBuilder MapHarvesterEndpoints(this IEndpointRouteBuilder routes)
    {
        var grp = routes.MapGroup("/api").RequireAuthorization();

        grp.MapGet("/inbox", async (HarvesterDbContext db, CardSyncState cards, CancellationToken ct) =>
        {
            // Fast path: card read model is populated → single index-only
            // scan over InboxCards, no joins, no per-row Article hydration.
            // Carries the pre-rendered chosen/other variant JSON so the
            // response is a near-direct projection of one table.
            if (cards.IsReady)
            {
                return Results.Ok(await ReadInboxFromCardsAsync(db, ct));
            }

            // Cold path: cards not yet built (first boot after migration).
            // The original Titles+Articles join carries us until backfill
            // finishes, after which all subsequent requests take the fast path.
            return Results.Ok(await ReadInboxFromBaseAsync(db, ct));
        });

        grp.MapPost("/scan", (ScanTrigger trigger) =>
        {
            trigger.RequestScan();
            return Results.Accepted();
        });

        grp.MapGet("/scans", async (HarvesterDbContext db, CancellationToken ct) =>
        {
            var runs = await db.ScanRuns
                .OrderByDescending(s => s.StartedAt)
                .Take(20)
                .AsNoTracking()
                .ToListAsync(ct);
            return Results.Ok(runs);
        });

        grp.MapPost("/articles/{id:int}/skip", async (int id, SubmissionService svc, CancellationToken ct) =>
        {
            await svc.SkipArticleAsync(id, ct);
            return Results.Ok();
        });

        grp.MapPost("/articles/{id:int}/resolve", async (int id, SubmissionService svc, CancellationToken ct) =>
        {
            var links = await svc.ResolveArticleAsync(id, ct);
            return Results.Ok(links.Select(r => new ResolvedLinkDto(r.Id, r.Hoster, r.Url, r.EpisodeIndex, r.ResolvedAt)));
        });

        grp.MapPost("/articles/{id:int}/send", async (int id, SubmissionService svc, CancellationToken ct) =>
        {
            var sub = await svc.SendToDsmAsync(id, ct);
            return Results.Ok(new
            {
                submissionId = sub.Id,
                status = sub.Status.ToString(),
                response = sub.ResponseMessage,
                taskIds = JsonSerializer.Deserialize<List<string>>(sub.DsmTaskIdsJson) ?? new()
            });
        });

        grp.MapGet("/budget", async (HarvesterDbContext db, CancellationToken ct) =>
        {
            var now = DateTimeOffset.UtcNow;
            var row = await db.CapSolverSpends
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Year == now.Year && c.Month == now.Month, ct);
            return Results.Ok(new { spentUsd = row?.SpentUsd ?? 0m, calls = row?.Calls ?? 0 });
        });

        return routes;
    }

    /// <summary>
    /// Fast path. Single-table query against the InboxCards read model.
    /// Returns the same wire shape as the legacy reader by deserialising
    /// the pre-rendered Chosen / OtherVariants JSON the keeper wrote at
    /// every base-table mutation.
    /// </summary>
    private static async Task<List<InboxCardDto>> ReadInboxFromCardsAsync(HarvesterDbContext db, CancellationToken ct)
    {
        var cards = await db.InboxCards.AsNoTracking()
            .Where(c => c.Visible)
            .OrderByDescending(c => c.UpdatedAt)
            .Take(200)
            .ToListAsync(ct);

        return cards.Select(c => new InboxCardDto(
            TitleId: c.TitleId,
            CatalogTitleId: c.CatalogTitleId,
            DisplayTitle: c.DisplayTitle,
            Poster: c.Poster,
            Year: c.Year,
            Rating: c.Rating,
            Genres: SafeGenres(c.GenresJson),
            MetadataUncertain: c.MetadataUncertain,
            Kind: c.Kind,
            SeasonNumber: c.SeasonNumber,
            BetterAvailable: c.BetterAvailable,
            UpdatedAt: c.UpdatedAt,
            Chosen: JsonSerializer.Deserialize<ArticleDto>(c.ChosenArticleJson)!,
            OtherVariants: JsonSerializer.Deserialize<List<ArticleDto>>(c.OtherVariantsJson) ?? new()
        )).ToList();
    }

    /// <summary>
    /// Cold path. Used while cards aren't yet populated (first boot after
    /// the read-model migration). Identical wire output to the fast path,
    /// produced from base tables via the legacy join. Removed once the
    /// fast path has soaked.
    /// </summary>
    private static async Task<List<InboxCardDto>> ReadInboxFromBaseAsync(HarvesterDbContext db, CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-3);
        var titles = await db.Titles
            .Where(t => (t.Status == TitleStatus.InInbox || t.Status == TitleStatus.New)
                && t.Articles.Any(a => a.DiscoveredAt >= cutoff
                    && (a.Status == ArticleStatus.Parsed
                        || a.Status == ArticleStatus.Resolved
                        || a.Status == ArticleStatus.InInbox)))
            .OrderByDescending(t => t.UpdatedAt)
            .Take(200)
            .Include(t => t.Articles)
            .AsNoTracking()
            .AsSplitQuery()
            .ToListAsync(ct);

        var catalogIds = titles.Where(t => t.CatalogTitleId.HasValue).Select(t => t.CatalogTitleId!.Value).Distinct().ToList();
        var catalogById = catalogIds.Count == 0
            ? new Dictionary<int, CatalogTitleEntity>()
            : await db.CatalogTitles
                .Include(t => t.Metadata)
                .AsNoTracking()
                .Where(t => catalogIds.Contains(t.Id))
                .ToDictionaryAsync(t => t.Id, ct);

        return titles.Select(t =>
        {
            var visibleArticles = t.Articles
                .Where(a => a.Status == ArticleStatus.Parsed
                         || a.Status == ArticleStatus.Resolved
                         || a.Status == ArticleStatus.InInbox)
                .OrderByDescending(a => a.QualityScore)
                .ToList();
            var chosen = visibleArticles.FirstOrDefault();
            if (chosen is null) return null;

            return new InboxCardDto(
                TitleId: t.Id,
                CatalogTitleId: t.CatalogTitleId,
                DisplayTitle: t.DisplayTitle,
                Poster: t.CatalogTitleId is int cid && catalogById.TryGetValue(cid, out var catalog) ? catalog.TitlePoster : null,
                Year: t.CatalogTitleId is int cidYear && catalogById.TryGetValue(cidYear, out var catalogYear)
                    ? catalogYear.Metadata?.Year ?? t.Year
                    : t.Year,
                Rating: t.CatalogTitleId is int cid2 && catalogById.TryGetValue(cid2, out var catalog2) ? catalog2.Metadata?.VoteAverage : null,
                Genres: t.CatalogTitleId is int cid3 && catalogById.TryGetValue(cid3, out var catalog3) ? SafeGenres(catalog3.Metadata?.GenresJson) : new(),
                MetadataUncertain: t.CatalogTitleId is int cid4 && catalogById.TryGetValue(cid4, out var catalog4) && catalog4.Metadata?.MetadataUncertain == true,
                Kind: t.Kind.ToString(),
                SeasonNumber: t.SeasonNumber,
                BetterAvailable: t.Status == TitleStatus.Submitted &&
                                 t.BestSubmittedQualityScore is int s &&
                                 chosen.QualityScore > s,
                UpdatedAt: t.UpdatedAt,
                Chosen: ToArticleDto(chosen),
                OtherVariants: visibleArticles.Skip(1).Select(ToArticleDto).ToList()
            );
        })
        .Where(d => d is not null)
        .Select(d => d!)
        .ToList();
    }

    private static ArticleDto ToArticleDto(ArticleEntity a) => new(
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

    private static List<string> SafeGenres(string? json)
    {
        if (string.IsNullOrEmpty(json) || json == "[]") return new();
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? new(); }
        catch { return new(); }
    }

    public sealed record InboxCardDto(
        int TitleId, int? CatalogTitleId, string DisplayTitle, string? Poster,
        int? Year, double? Rating, List<string> Genres, bool MetadataUncertain,
        string Kind, int? SeasonNumber,
        bool BetterAvailable, DateTimeOffset UpdatedAt,
        ArticleDto Chosen, List<ArticleDto> OtherVariants);

    public sealed record ArticleDto(
        int Id, string Url, string QualityLabel, int QualityScore,
        string? Resolution, string? Codec, string? SourceTier, string? Language,
        long? SizeBytes, int? EpisodeCount,
        List<HosterSummaryDto> Hosters, string Status);

    public sealed record HosterSummaryDto(string Hoster, int LinkCount);

    public sealed record ResolvedLinkDto(int Id, string Hoster, string Url, int? EpisodeIndex, DateTimeOffset ResolvedAt);
}
