using LinkHarvester.Core;
using LinkHarvester.Persistence;
using LinkHarvester.Persistence.Catalog;
using LinkHarvester.Worker.Discovery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LinkHarvester.Api.Endpoints;

public static class DiscoveryEndpoints
{
    public static IEndpointRouteBuilder MapDiscoveryEndpoints(this IEndpointRouteBuilder routes)
    {
        var grp = routes.MapGroup("/api/discover").RequireAuthorization();

        // List the discovery wall — popularity rows joined with the catalog
        // and de-duped (a title that appears in multiple sources surfaces
        // every source's badge).
        grp.MapGet("", async (
            HarvesterDbContext db,
            ISettingsService settings,
            [FromQuery] string? source = null,
            [FromQuery] bool hideAlreadyGrabbed = true,
            [FromQuery] string? kind = null,
            [FromQuery] int limit = 60,
            CancellationToken ct = default) =>
        {
            limit = Math.Clamp(limit, 1, 200);
            var entries = db.DiscoveryEntries.AsNoTracking().AsQueryable();
            if (!string.IsNullOrWhiteSpace(source))
                entries = entries.Where(d => d.Source == source);

            var raw = await entries
                .OrderBy(d => d.Rank)
                .Take(limit * 4)
                .ToListAsync(ct);
            if (raw.Count == 0) return Results.Ok(new DiscoveryPageDto(new()));

            var titleIds = raw.Select(d => d.CatalogTitleId).Distinct().ToList();
            var titles = await db.CatalogTitles.AsNoTracking()
                .Where(t => titleIds.Contains(t.Id) && !t.IsHidden)
                .Include(t => t.Metadata)
                .Include(t => t.Links)
                .Include(t => t.Episodes).ThenInclude(e => e.Links)
                .ToListAsync(ct);

            // Already-grabbed lookup.
            HashSet<int> grabbed = new();
            if (hideAlreadyGrabbed)
            {
                var grabbedIds = await db.Submissions.AsNoTracking()
                    .Where(s => s.Status == SubmissionStatus.Sent && s.CatalogTitleId != null
                             && titleIds.Contains(s.CatalogTitleId!.Value))
                    .Select(s => s.CatalogTitleId!.Value)
                    .Distinct()
                    .ToListAsync(ct);
                grabbed = grabbedIds.ToHashSet();
            }

            if (!string.IsNullOrWhiteSpace(kind))
            {
                var movieCats = new[] { "Films", "Movie" };
                var tvCats = new[] { "Séries", "Series", "Animes" };
                if (string.Equals(kind, "movie", StringComparison.OrdinalIgnoreCase))
                    titles = titles.Where(t => movieCats.Contains(t.CategoryName)).ToList();
                else if (string.Equals(kind, "tv", StringComparison.OrdinalIgnoreCase))
                    titles = titles.Where(t => tvCats.Contains(t.CategoryName)).ToList();
            }

            var s = settings.Current;
            var byTitle = raw.GroupBy(d => d.CatalogTitleId).ToDictionary(g => g.Key, g => g.ToList());

            var results = titles
                .Select(t =>
                {
                    if (!byTitle.TryGetValue(t.Id, out var sources)) return null;
                    var allLinks = t.Links.Concat(t.Episodes.SelectMany(e => e.Links)).ToList();
                    var best = BestVariantPicker.Pick(allLinks, s.HosterPriority, s.EffectiveQualityPreference, s.AudioPreference);
                    if (best is null) return null;
                    var sourceDtos = sources
                        .OrderBy(x => x.Rank)
                        .Select(x => new DiscoverySourceDto(x.Source, x.Rank, x.Reason))
                        .ToList();
                    var minRank = sources.Min(x => x.Rank);
                    return new
                    {
                        Dto = new DiscoveryHitDto(
                            TitleId: t.Id,
                            Title: t.TitleName,
                            Year: t.Metadata?.Year,
                            Category: t.CategoryName,
                            Poster: t.TitlePoster,
                            LinkCount: t.LinkCount,
                            BestLink: new CatalogEndpoints.LookupLinkDto(
                                LinkId: best.Id,
                                Quality: best.QualityName,
                                AudioLangs: best.AudioLangs,
                                Host: best.HostName,
                                SizeBytes: best.SizeBytes),
                            Sources: sourceDtos,
                            AlreadyGrabbed: grabbed.Contains(t.Id)),
                        Rank = minRank
                    };
                })
                .Where(x => x is not null)
                .Select(x => x!)
                .Where(x => !hideAlreadyGrabbed || !x.Dto.AlreadyGrabbed)
                .OrderBy(x => x.Rank)
                .Take(limit)
                .Select(x => x.Dto)
                .ToList();

            return Results.Ok(new DiscoveryPageDto(results));
        });

        grp.MapPost("/refresh", async (DiscoveryRefreshService svc, CancellationToken ct) =>
        {
            var summary = await svc.RunOnceAsync(ct);
            return Results.Ok(new
            {
                tmdb = summary.TmdbInserted,
                trakt = summary.TraktInserted,
                letterboxd = summary.LetterboxdInserted,
                plex = summary.PlexInserted,
                pruned = summary.Pruned
            });
        });

        return routes;
    }

    public sealed record DiscoverySourceDto(string Source, int Rank, string Reason);

    public sealed record DiscoveryHitDto(
        int TitleId, string Title, int? Year, string Category, string? Poster,
        int LinkCount, CatalogEndpoints.LookupLinkDto BestLink,
        List<DiscoverySourceDto> Sources, bool AlreadyGrabbed);

    public sealed record DiscoveryPageDto(List<DiscoveryHitDto> Results);
}
