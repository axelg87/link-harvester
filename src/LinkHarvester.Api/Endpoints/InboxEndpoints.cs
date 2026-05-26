using System.Text.Json;
using LinkHarvester.Core;
using LinkHarvester.Persistence;
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

        grp.MapGet("/inbox", async (HarvesterDbContext db, CancellationToken ct) =>
        {
            // Group by Title. For each title return the variants we should display.
            var titles = await db.Titles
                .Where(t => t.Status == TitleStatus.InInbox || t.Status == TitleStatus.New)
                .OrderByDescending(t => t.UpdatedAt)
                .Take(200)
                .Include(t => t.Articles)
                .AsNoTracking()
                .ToListAsync(ct);
            var cutoff = DateTimeOffset.UtcNow.AddDays(-3);
            titles = titles
                .Where(t => t.Articles.Any(a => a.DiscoveredAt >= cutoff &&
                    (a.Status == ArticleStatus.Parsed || a.Status == ArticleStatus.Resolved || a.Status == ArticleStatus.InInbox)))
                .ToList();

            var catalogIds = titles.Where(t => t.CatalogTitleId.HasValue).Select(t => t.CatalogTitleId!.Value).Distinct().ToList();
            var catalogById = catalogIds.Count == 0
                ? new Dictionary<int, CatalogTitleEntity>()
                : await db.CatalogTitles
                    .Include(t => t.Metadata)
                    .AsNoTracking()
                    .Where(t => catalogIds.Contains(t.Id))
                    .ToDictionaryAsync(t => t.Id, ct);

            var dto = titles.Select(t =>
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
                    Year: t.Year,
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
            .ToList();

            return Results.Ok(dto);
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
