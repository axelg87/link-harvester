using System.Globalization;
using System.Text.Json;
using LinkHarvester.Api.Caching;
using LinkHarvester.Api.Maintenance;
using LinkHarvester.Core;
using LinkHarvester.Enrichment;
using LinkHarvester.Persistence;
using LinkHarvester.Persistence.Catalog;
using LinkHarvester.Synology;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LinkHarvester.Api.Endpoints;

public static class CatalogEndpoints
{
    public static IEndpointRouteBuilder MapCatalogEndpoints(this IEndpointRouteBuilder routes)
    {
        var grp = routes.MapGroup("/api/catalog").RequireAuthorization();

        // ── Browse ───────────────────────────────────────────────────────────
        grp.MapGet("/search", async (
            HarvesterDbContext db,
            [FromQuery] string? q,
            [FromQuery] string? category,
            [FromQuery] string? hosts,         // csv
            [FromQuery] string? qualities,     // csv
            [FromQuery] string? audio,         // csv
            [FromQuery] string? genres,        // csv
            [FromQuery] int? yearMin,
            [FromQuery] int? yearMax,
            [FromQuery] double? ratingMin,
            [FromQuery] int? runtimeMin,
            [FromQuery] int? runtimeMax,
            [FromQuery] long? sizeMinBytes,
            [FromQuery] long? sizeMaxBytes,
            [FromQuery] string? originalLanguage,
            [FromQuery] bool? hasMetadata,
            [FromQuery] string sort = "popularity",
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 50,
            CancellationToken ct = default) =>
        {
            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 1, 100);
            var offset = (page - 1) * pageSize;

            // 1) Resolve candidate title ids from FTS (if q provided) or from titles directly.
            // FTS5 isn't reachable through a single LINQ expression, so when q is set we
            // materialise the matching rowids into memory first. The set is bounded by
            // FTS itself (a single search term across a 2M-row table returns hundreds
            // to a few thousand ids, comfortably small to pass via a parameterised list).
            IEnumerable<int>? ftsIds = null;
            if (!string.IsNullOrWhiteSpace(q))
            {
                var escaped = SanitizeFtsQuery(q);
                ftsIds = await db.Database.SqlQueryRaw<int>(
                    "SELECT rowid AS Value FROM CatalogTitlesFts WHERE CatalogTitlesFts MATCH {0} LIMIT 5000",
                    escaped).ToListAsync(ct);
            }

            var titles = db.CatalogTitles.AsNoTracking().AsQueryable();
            if (ftsIds is not null)
            {
                var ids = ftsIds.ToList();
                titles = titles.Where(t => ids.Contains(t.Id));
            }

            if (!string.IsNullOrWhiteSpace(category))
                titles = titles.Where(t => t.CategoryName == category);

            // Hoster / quality / audio / size filters require joining links.
            var needLinkFilter = !string.IsNullOrWhiteSpace(hosts)
                              || !string.IsNullOrWhiteSpace(qualities)
                              || !string.IsNullOrWhiteSpace(audio)
                              || sizeMinBytes.HasValue || sizeMaxBytes.HasValue;
            if (needLinkFilter)
            {
                var hostSet = CsvSet(hosts).Select(h => h.ToLowerInvariant()).ToHashSet();
                var qualSet = CsvSet(qualities).ToHashSet();
                var audioSet = CsvSet(audio);
                titles = titles.Where(t => t.Links.Any(l =>
                    (hostSet.Count == 0 || hostSet.Contains(l.NormalizedHost)) &&
                    (qualSet.Count == 0 || (l.QualityName != null && qualSet.Contains(l.QualityName))) &&
                    (audioSet.Count == 0 || (l.AudioLangs != null && audioSet.Any(a => l.AudioLangs.Contains(a)))) &&
                    (!sizeMinBytes.HasValue || (l.SizeBytes != null && l.SizeBytes >= sizeMinBytes)) &&
                    (!sizeMaxBytes.HasValue || (l.SizeBytes != null && l.SizeBytes <= sizeMaxBytes))));
            }

            // Metadata filters.
            var needMetaJoin = yearMin.HasValue || yearMax.HasValue || ratingMin.HasValue
                            || runtimeMin.HasValue || runtimeMax.HasValue
                            || !string.IsNullOrWhiteSpace(genres)
                            || !string.IsNullOrWhiteSpace(originalLanguage)
                            || (hasMetadata ?? false);

            var joined = titles
                .Select(t => new
                {
                    t.Id, t.TitleName, t.OriginalTitle, t.CategoryName, t.TitlePoster,
                    t.LinkCount, t.EpisodeCount,
                    Meta = t.Metadata
                });

            if (yearMin.HasValue) joined = joined.Where(x => x.Meta != null && x.Meta.Year >= yearMin);
            if (yearMax.HasValue) joined = joined.Where(x => x.Meta != null && x.Meta.Year <= yearMax);
            if (ratingMin.HasValue) joined = joined.Where(x => x.Meta != null && x.Meta.VoteAverage >= ratingMin);
            if (runtimeMin.HasValue) joined = joined.Where(x => x.Meta != null && x.Meta.Runtime >= runtimeMin);
            if (runtimeMax.HasValue) joined = joined.Where(x => x.Meta != null && x.Meta.Runtime <= runtimeMax);
            if (!string.IsNullOrWhiteSpace(originalLanguage))
                joined = joined.Where(x => x.Meta != null && x.Meta.OriginalLanguage == originalLanguage);
            if (hasMetadata == true)
                joined = joined.Where(x => x.Meta != null && (x.Meta.EnrichmentSource == "tmdb_direct" || x.Meta.EnrichmentSource == "tmdb_imdb_lookup"));

            if (!string.IsNullOrWhiteSpace(genres))
            {
                var genreSet = CsvSet(genres);
                joined = joined.Where(x => x.Meta != null && genreSet.Any(g => x.Meta.GenresJson.Contains(g)));
            }

            var totalCount = await joined.CountAsync(ct);

            joined = sort switch
            {
                "year" => joined.OrderByDescending(x => x.Meta!.Year).ThenBy(x => x.TitleName),
                "rating" => joined.OrderByDescending(x => x.Meta!.VoteAverage).ThenBy(x => x.TitleName),
                "title" => joined.OrderBy(x => x.TitleName),
                _ => joined.OrderByDescending(x => x.Meta!.Popularity).ThenBy(x => x.TitleName)
            };

            var page1 = await joined.Skip(offset).Take(pageSize).ToListAsync(ct);

            var dto = page1.Select(x => new SearchHitDto(
                Id: x.Id,
                Title: x.TitleName,
                OriginalTitle: x.OriginalTitle,
                Category: x.CategoryName,
                Poster: x.TitlePoster,
                LinkCount: x.LinkCount,
                EpisodeCount: x.EpisodeCount,
                Year: x.Meta?.Year,
                Rating: x.Meta?.VoteAverage,
                Runtime: x.Meta?.Runtime,
                Overview: x.Meta?.Overview,
                Genres: SafeGenres(x.Meta?.GenresJson),
                OriginalLanguage: x.Meta?.OriginalLanguage,
                EnrichmentSource: x.Meta?.EnrichmentSource
            )).ToList();

            return Results.Ok(new SearchPageDto(totalCount, page, pageSize, dto));
        });

        // ── One title with all its variants ──────────────────────────────────
        grp.MapGet("/titles/{id:int}", async (int id, HarvesterDbContext db, CancellationToken ct) =>
        {
            var t = await db.CatalogTitles.AsNoTracking()
                .Include(x => x.Metadata)
                .Include(x => x.Episodes)
                .Include(x => x.Links)
                .FirstOrDefaultAsync(x => x.Id == id, ct);
            if (t is null) return Results.NotFound();

            var dto = new TitleDetailDto(
                Id: t.Id,
                Title: t.TitleName,
                OriginalTitle: t.OriginalTitle,
                Category: t.CategoryName,
                Poster: t.TitlePoster,
                Year: t.Metadata?.Year,
                Rating: t.Metadata?.VoteAverage,
                Runtime: t.Metadata?.Runtime,
                Overview: t.Metadata?.Overview,
                Genres: SafeGenres(t.Metadata?.GenresJson),
                OriginalLanguage: t.Metadata?.OriginalLanguage,
                ImdbId: t.ImdbId,
                TmdbId: t.TmdbId,
                Links: t.Links.Where(l => l.EpisodeId == null).Select(MapLink).ToList(),
                Episodes: t.Episodes.OrderBy(e => e.IsFullSeason ? 0 : 1)
                                     .ThenBy(e => e.SeasonNumber).ThenBy(e => e.EpisodeNumber)
                                     .Select(e => new EpisodeDto(
                                        e.Id, e.SeasonNumber, e.EpisodeNumber, e.EpisodeName, e.IsFullSeason,
                                        t.Links.Where(l => l.EpisodeId == e.Id).Select(MapLink).ToList()))
                                     .ToList());
            return Results.Ok(dto);
        });

        // ── Facets (filter chip counts) ──────────────────────────────────────
        // Both /facets and /genres are full-table scans; the result barely
        // changes between catalog page loads. Cache for 5 minutes via
        // CatalogAggregatesCache (single-flight per key) so the catalog
        // page doesn't kick off four heavy queries on every render.
        grp.MapGet("/facets", async (HarvesterDbContext db, CatalogAggregatesCache cache, CancellationToken ct) =>
        {
            var result = await cache.GetOrAddAsync("facets:v1", async token =>
            {
                // EF Core's SQLite translator dislikes positional record constructors
                // inside Select; project to anonymous first, then map.
                var categories = await db.CatalogTitles.AsNoTracking()
                    .GroupBy(t => t.CategoryName)
                    .Select(g => new { Key = g.Key, Count = g.Count() })
                    .ToListAsync(token);
                var hosts = await db.CatalogLinks.AsNoTracking()
                    .GroupBy(l => l.HostName)
                    .Select(g => new { Key = g.Key, Count = g.Count() })
                    .OrderByDescending(x => x.Count)
                    .Take(40)
                    .ToListAsync(token);
                var qualities = await db.CatalogLinks.AsNoTracking()
                    .Where(l => l.QualityName != null)
                    .GroupBy(l => l.QualityName!)
                    .Select(g => new { Key = g.Key, Count = g.Count() })
                    .OrderByDescending(x => x.Count)
                    .Take(40)
                    .ToListAsync(token);
                var audio = await db.CatalogLinks.AsNoTracking()
                    .Where(l => l.AudioLangs != null)
                    .GroupBy(l => l.AudioLangs!)
                    .Select(g => new { Key = g.Key, Count = g.Count() })
                    .OrderByDescending(x => x.Count)
                    .Take(40)
                    .ToListAsync(token);
                return new FacetsResponse(
                    categories.Select(x => new FacetEntry(x.Key, x.Count)).ToList(),
                    hosts.Select(x => new FacetEntry(x.Key, x.Count)).ToList(),
                    qualities.Select(x => new FacetEntry(x.Key, x.Count)).ToList(),
                    audio.Select(x => new FacetEntry(x.Key, x.Count)).ToList());
            }, ct: ct);
            return Results.Ok(result);
        });

        grp.MapGet("/genres", async (HarvesterDbContext db, CatalogAggregatesCache cache, CancellationToken ct) =>
        {
            var result = await cache.GetOrAddAsync("genres:v1", async token =>
            {
                var rows = await db.CatalogTitleMetadata.AsNoTracking()
                    .Where(m => m.GenresJson != "[]")
                    .Select(m => m.GenresJson)
                    .ToListAsync(token);
                var counts = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var json in rows)
                {
                    try
                    {
                        var arr = JsonSerializer.Deserialize<string[]>(json);
                        if (arr is null) continue;
                        foreach (var g in arr) counts[g] = counts.TryGetValue(g, out var n) ? n + 1 : 1;
                    }
                    catch { /* malformed JSON row — skip */ }
                }
                return counts.Select(kv => new FacetEntry(kv.Key, kv.Value))
                    .OrderByDescending(x => x.Count).Take(40).ToList();
            }, ct: ct);
            return Results.Ok(result);
        });

        // ── Send specific link(s) to DSM right from the catalog ─────────────
        grp.MapPost("/links/send", async (
            [FromBody] SendLinksReq req,
            HarvesterDbContext db,
            IDownloadStationClient dsm,
            ISettingsService settings,
            CancellationToken ct) =>
        {
            if (req.LinkIds is null || req.LinkIds.Count == 0)
                return Results.BadRequest(new { error = "no linkIds provided" });

            var links = await db.CatalogLinks.AsNoTracking()
                .Include(l => l.Title)
                .Where(l => req.LinkIds.Contains(l.Id))
                .ToListAsync(ct);
            if (links.Count == 0) return Results.NotFound();

            var urls = links.Select(l => l.LinkUrl).Distinct().ToList();
            var kindIsMovie = links.All(l => string.Equals(l.Title.CategoryName, "Films", StringComparison.OrdinalIgnoreCase));
            var dest = kindIsMovie
                ? settings.Current.SynologyMovieDestination
                : settings.Current.SynologySeriesDestination;

            try
            {
                var taskIds = await dsm.CreateTasksAsync(urls, dest, ct);
                return Results.Ok(new SendResultDto(true, null, taskIds.ToList(), urls.Count));
            }
            catch (DsmException dx)
            {
                // Map to a proper HTTP status so the client can branch on it,
                // but keep ok=false in the body so the existing WASM toast
                // path keeps working without changes.
                var status = dx.Code switch
                {
                    DsmException.SyntheticNotConfigured => StatusCodes.Status400BadRequest,
                    DsmException.SyntheticUnreachable => StatusCodes.Status502BadGateway,
                    DsmException.SyntheticTimeout => StatusCodes.Status504GatewayTimeout,
                    _ => StatusCodes.Status502BadGateway,
                };
                return Results.Json(
                    new SendResultDto(false, dx.HumanMessage, new(), urls.Count),
                    statusCode: status);
            }
        });

        grp.MapGet("/stats", async (HarvesterDbContext db, TmdbStatusTracker tmdb, CancellationToken ct) =>
        {
            var titles = await db.CatalogTitles.CountAsync(ct);
            var links = await db.CatalogLinks.CountAsync(ct);
            var episodes = await db.CatalogEpisodes.CountAsync(ct);
            var enriched = await db.CatalogTitleMetadata.CountAsync(m =>
                m.EnrichmentSource == "tmdb_direct" || m.EnrichmentSource == "tmdb_imdb_lookup", ct);
            var failed = await db.CatalogTitleMetadata.CountAsync(m => m.EnrichmentSource == "failed", ct);

            // Subset of the failed bucket that looks like SQLite write
            // contention or a 30-second command-timeout — i.e. transient
            // infra noise that the user can safely retry without risking
            // a wasted TMDB call. Patterns live in EnrichmentMaintenance so
            // the count, the manual reset endpoint, and the eventual
            // BUG-2 startup auto-heal stay in lock-step.
            var transientFailed = await EnrichmentMaintenance.CountTransientFailedAsync(db, ct);

            return Results.Ok(new
            {
                titles,
                links,
                episodes,
                enriched,
                enrichmentFailed = failed,
                enrichmentFailedTransient = transientFailed,
                enrichmentRunState = tmdb.State
            });
        });

        // Reset enrichment failures so the background enricher will retry
        // them on its next batch. With ?onlyLockErrors=true (the default),
        // resets only rows that look like SQLite contention or command
        // timeout. Without the flag, resets every 'failed' row regardless
        // of cause.
        grp.MapPost("/enrichment/reset-failed", async (
            HarvesterDbContext db,
            ILoggerFactory loggerFactory,
            [FromQuery] bool onlyLockErrors = true,
            CancellationToken ct = default) =>
        {
            var log = loggerFactory.CreateLogger("EnrichmentReset");
            var reset = onlyLockErrors
                ? await EnrichmentMaintenance.ResetTransientFailedAsync(db, ct)
                : await EnrichmentMaintenance.ResetAllFailedAsync(db, ct);
            log.LogInformation(
                "manual reset: {Count} enrichment failures requeued (onlyLockErrors={Mode})",
                reset, onlyLockErrors);
            return Results.Ok(new { reset });
        });

        return routes;
    }

    private static LinkDto MapLink(CatalogLinkEntity l) => new(
        Id: l.Id, Url: l.LinkUrl, Host: l.HostName, Quality: l.QualityName,
        AudioLangs: l.AudioLangs, SubLangs: l.SubLangs, SizeBytes: l.SizeBytes);

    private static List<string> SafeGenres(string? json)
    {
        if (string.IsNullOrEmpty(json) || json == "[]") return new();
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? new(); }
        catch { return new(); }
    }

    private static HashSet<string> CsvSet(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return new();
        return csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string SanitizeFtsQuery(string raw)
    {
        // Allow only word chars + spaces; FTS5 prefix search per token.
        var cleaned = new string(raw.Select(c => char.IsLetterOrDigit(c) || c == ' ' ? c : ' ').ToArray());
        var tokens = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return "\"\"";
        return string.Join(' ', tokens.Select(t => $"\"{t}\"*"));
    }

    public sealed record SearchHitDto(
        int Id, string Title, string? OriginalTitle, string Category, string? Poster,
        int LinkCount, int EpisodeCount,
        int? Year, double? Rating, int? Runtime, string? Overview,
        List<string> Genres, string? OriginalLanguage, string? EnrichmentSource);

    public sealed record SearchPageDto(int Total, int Page, int PageSize, List<SearchHitDto> Items);

    public sealed record TitleDetailDto(
        int Id, string Title, string? OriginalTitle, string Category, string? Poster,
        int? Year, double? Rating, int? Runtime, string? Overview,
        List<string> Genres, string? OriginalLanguage, string? ImdbId, int? TmdbId,
        List<LinkDto> Links, List<EpisodeDto> Episodes);

    public sealed record EpisodeDto(int Id, int SeasonNumber, int EpisodeNumber,
        string? EpisodeName, bool IsFullSeason, List<LinkDto> Links);

    public sealed record LinkDto(int Id, string Url, string Host, string? Quality,
        string? AudioLangs, string? SubLangs, long? SizeBytes);

    public sealed record FacetEntry(string Key, int Count);

    public sealed record FacetsResponse(
        List<FacetEntry> Categories,
        List<FacetEntry> Hosts,
        List<FacetEntry> Qualities,
        List<FacetEntry> Audio);

    public sealed record SendLinksReq(List<int> LinkIds);
    public sealed record SendResultDto(bool Ok, string? Error, List<string> TaskIds, int Submitted);
}
