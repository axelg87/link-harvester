using System.Globalization;
using System.Text.Json;
using LinkHarvester.Api.Caching;
using LinkHarvester.Api.Maintenance;
using LinkHarvester.Core;
using LinkHarvester.Enrichment;
using LinkHarvester.Persistence;
using LinkHarvester.Persistence.Catalog;
using LinkHarvester.Synology;
using LinkHarvester.Worker;
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
            [FromQuery] bool? includeHidden,
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
            if (!(includeHidden ?? false))
                titles = titles.Where(t => !t.IsHidden);
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
                    Meta = t.Metadata,
                    // For series-season cards: if every episode of this title
                    // belongs to one season, surface it for the tile + the
                    // inventory lookup. Multi-season aggregates leave it null.
                    SeasonMin = t.Episodes.Where(e => e.SeasonNumber > 0).Select(e => (int?)e.SeasonNumber).Min(),
                    SeasonMax = t.Episodes.Where(e => e.SeasonNumber > 0).Select(e => (int?)e.SeasonNumber).Max(),
                });

            if (yearMin.HasValue) joined = joined.Where(x => x.Meta != null && x.Meta.Year >= yearMin);
            if (yearMax.HasValue) joined = joined.Where(x => x.Meta != null && x.Meta.Year <= yearMax);
            if (ratingMin.HasValue) joined = joined.Where(x => x.Meta != null && x.Meta.VoteAverage >= ratingMin);
            if (runtimeMin.HasValue) joined = joined.Where(x => x.Meta != null && x.Meta.Runtime >= runtimeMin);
            if (runtimeMax.HasValue) joined = joined.Where(x => x.Meta != null && x.Meta.Runtime <= runtimeMax);
            if (!string.IsNullOrWhiteSpace(originalLanguage))
                joined = joined.Where(x => x.Meta != null && x.Meta.OriginalLanguage == originalLanguage);
            if (hasMetadata == true)
                joined = joined.Where(x => x.Meta != null && x.Meta.EnrichmentSource.StartsWith("tmdb_"));

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
                EnrichmentSource: x.Meta?.EnrichmentSource,
                MetadataUncertain: x.Meta?.MetadataUncertain == true,
                SeasonNumber: x.SeasonMin.HasValue && x.SeasonMin == x.SeasonMax ? x.SeasonMin : null
            )).ToList();

            return Results.Ok(new SearchPageDto(totalCount, page, pageSize, dto));
        });

        // ── One title with all its variants ──────────────────────────────────
        grp.MapGet("/titles/{id:int}", async (int id, HarvesterDbContext db, CancellationToken ct) =>
        {
            var t = await db.CatalogTitles.AsNoTracking()
                .Include(x => x.Metadata)
                .FirstOrDefaultAsync(x => x.Id == id, ct);
            if (t is null) return Results.NotFound();

            var episodes = await db.CatalogEpisodes.AsNoTracking()
                .Where(e => e.TitleId == id)
                .OrderBy(e => e.IsFullSeason ? 0 : 1)
                .ThenBy(e => e.SeasonNumber)
                .ThenBy(e => e.EpisodeNumber)
                .ToListAsync(ct);
            var links = await db.CatalogLinks.AsNoTracking()
                .Where(l => l.TitleId == id)
                .OrderBy(l => l.EpisodeId == null ? 0 : 1)
                .ThenBy(l => l.EpisodeId)
                .ThenBy(l => l.HostName)
                .ToListAsync(ct);
            var linksByEpisode = links
                .Where(l => l.EpisodeId.HasValue)
                .GroupBy(l => l.EpisodeId!.Value)
                .ToDictionary(g => g.Key, g => g.Select(MapLink).ToList());

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
                MetadataUncertain: t.Metadata?.MetadataUncertain == true,
                Links: links.Where(l => l.EpisodeId == null).Select(MapLink).ToList(),
                Episodes: episodes
                    .Select(e => new EpisodeDto(
                        e.Id,
                        e.SeasonNumber,
                        e.EpisodeNumber,
                        e.EpisodeName,
                        e.IsFullSeason,
                        linksByEpisode.TryGetValue(e.Id, out var episodeLinks) ? episodeLinks : new()))
                    .ToList());
            return Results.Ok(dto);
        });

        // ── What's on disk for this title ────────────────────────────────────
        // Per-click DSM lookup: when the user opens a season card, list the
        // matching season folder and return the file names. Two FileStation
        // calls in the steady state (title folder → season folder) so the user
        // sees a 300-600ms shimmer rather than a multi-second scan. No
        // background reconciler — we only look when asked.
        grp.MapGet("/titles/{id:int}/inventory", async (
            int id,
            HarvesterDbContext db,
            ISettingsService settings,
            IDownloadStationClient dsm,
            CancellationToken ct) =>
        {
            var title = await db.CatalogTitles.AsNoTracking()
                .Include(t => t.Episodes)
                .FirstOrDefaultAsync(t => t.Id == id, ct);
            if (title is null) return Results.NotFound();

            var s = settings.Current;
            // Inventory only makes sense for series/anime where the title has
            // its own folder. Movies drop into a flat root mixed with other
            // movies; listing it would return thousands of unrelated files.
            var isFolderedKind = string.Equals(title.CategoryName, "Animes", StringComparison.OrdinalIgnoreCase)
                              || string.Equals(title.CategoryName, "Séries", StringComparison.OrdinalIgnoreCase)
                              || string.Equals(title.CategoryName, "Series", StringComparison.OrdinalIgnoreCase);
            if (!isFolderedKind)
            {
                return Results.Ok(InventoryDto.Missing(string.Empty, null));
            }

            var season = FirstSeasonNumber(title);
            var plan = MatchPlan(title, s);
            // For series/anime, ParentSegments is [titlePath, seasonPath]; the
            // title-level folder is the next-to-last entry. For miniseries
            // with no season, there's only the title folder.
            var titlePath = plan.ParentSegments.Count >= 2
                ? plan.ParentSegments[^2]
                : (plan.ParentSegments.Count == 1 ? plan.ParentSegments[0] : plan.FullPath);

            try
            {
                if (season is int n && n > 0)
                {
                    var titleListed = await dsm.ListFolderAsync(titlePath, ct);
                    if (!titleListed.Exists)
                    {
                        return Results.Ok(InventoryDto.Missing(titlePath, season));
                    }
                    var seasonDir = titleListed.Files
                        .FirstOrDefault(f => f.IsDir && MatchesSeasonFolder(f.Name, n));
                    if (seasonDir is null)
                    {
                        return Results.Ok(InventoryDto.Missing($"{titlePath}/<Season {n}>", season));
                    }
                    var seasonListed = await dsm.ListFolderAsync($"{titlePath}/{seasonDir.Name}", ct);
                    return Results.Ok(InventoryDto.From(seasonListed, season));
                }
                else
                {
                    // No clear season → just walk the title folder (movies, or
                    // multi-season series we can't disambiguate).
                    var listed = await dsm.ListFolderAsync(titlePath, ct);
                    return Results.Ok(InventoryDto.From(listed, season));
                }
            }
            catch (DsmException ex)
            {
                return Results.Ok(new InventoryDto(
                    Ok: false, Error: ex.HumanMessage, Exists: false,
                    FolderPath: titlePath, SeasonNumber: season, Files: new()));
            }
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
            SubmissionService submissions,
            CancellationToken ct) =>
        {
            if (req.LinkIds is null || req.LinkIds.Count == 0)
                return Results.BadRequest(new { error = "no linkIds provided" });

            var links = await db.CatalogLinks.AsNoTracking()
                .Include(l => l.Title)
                .ThenInclude(t => t.Episodes)
                .Where(l => req.LinkIds.Contains(l.Id))
                .ToListAsync(ct);
            if (links.Count == 0) return Results.NotFound();

            var ztLinks = links
                .Where(l => string.Equals(l.LinkSource, "zt", StringComparison.OrdinalIgnoreCase) && l.HarvesterArticleId.HasValue)
                .GroupBy(l => l.HarvesterArticleId!.Value)
                .Select(g => g.First())
                .ToList();
            if (ztLinks.Count > 0)
            {
                var taskIds = new List<string>();
                var errors = new List<string>();
                foreach (var zt in ztLinks)
                {
                    var sub = await submissions.SendToDsmAsync(zt.HarvesterArticleId!.Value, ct);
                    if (sub.Status == SubmissionStatus.Sent)
                        taskIds.AddRange(JsonSerializer.Deserialize<List<string>>(sub.DsmTaskIdsJson) ?? new());
                    else
                        errors.Add(sub.ResponseMessage ?? $"ZT send failed for article {zt.HarvesterArticleId}");
                }

                if (links.Count == ztLinks.Count)
                {
                    return Results.Ok(new SendResultDto(
                        Ok: errors.Count == 0,
                        Error: errors.Count == 0 ? null : string.Join(" | ", errors),
                        TaskIds: taskIds,
                        Submitted: ztLinks.Count));
                }
            }

            var directLinks = links
                .Where(l => !string.Equals(l.LinkSource, "zt", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var urls = directLinks.Select(l => l.LinkUrl).Distinct().ToList();
            if (urls.Count == 0) return Results.BadRequest(new { error = "no direct catalog links provided" });

            // Kind-aware destination — if every selected link belongs to a
            // single title we use <Root>/<Title>/Season N; otherwise (mixed
            // batch) we fall back to the kind root with no per-title subfolder.
            var dest = BuildCatalogDestination(links, settings.Current);
            var foldersToEnsure = CatalogFoldersToEnsure(links, settings.Current);
            if (foldersToEnsure.Count > 0)
            {
                try { await dsm.EnsureFoldersAsync(foldersToEnsure, ct); }
                catch (DsmException dx)
                {
                    return Results.BadRequest(new { error = $"Could not create destination folder: {dx.HumanMessage}" });
                }
            }

            var attempt = new SubmissionEntity
            {
                Source = SendSourceKind.Catalog,
                CatalogTitleId = links.Select(l => l.TitleId).Distinct().Count() == 1 ? links[0].TitleId : null,
                CatalogLinkIdsJson = JsonSerializer.Serialize(directLinks.Select(l => l.Id).ToList()),
                DisplayTitle = links.Select(l => l.Title.TitleName).Distinct().Count() == 1 ? links[0].Title.TitleName : $"{links.Count} catalog links",
                Destination = dest,
                UrlCount = urls.Count,
                SubmittedUrlsJson = JsonSerializer.Serialize(urls),
                Status = SubmissionStatus.Pending,
                SubmittedAt = DateTimeOffset.UtcNow
            };
            db.Submissions.Add(attempt);
            await db.SaveChangesAsync(ct);

            try
            {
                var taskIds = await dsm.CreateTasksAsync(urls, dest, ct);
                attempt.Status = SubmissionStatus.Sent;
                attempt.DsmTaskIdsJson = JsonSerializer.Serialize(taskIds);
                attempt.CompletedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
                return Results.Ok(new SendResultDto(true, null, taskIds.ToList(), urls.Count));
            }
            catch (DsmException dx)
            {
                attempt.Status = SubmissionStatus.Failed;
                attempt.ResponseMessage = dx.HumanMessage;
                attempt.DsmErrorCode = dx.Code;
                attempt.DsmFailedUrl = dx.FailedUrl;
                attempt.CompletedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
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
            var enriched = await db.CatalogTitleMetadata.CountAsync(m => m.EnrichmentSource.StartsWith("tmdb_"), ct);
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
        Id: l.Id, Url: l.LinkUrl, Host: string.IsNullOrWhiteSpace(l.HostName) ? "Unknown" : l.HostName, Quality: l.QualityName,
        AudioLangs: l.AudioLangs, SubLangs: l.SubLangs, SizeBytes: l.SizeBytes,
        Source: string.IsNullOrWhiteSpace(l.LinkSource) ? "catalog" : l.LinkSource, HarvesterArticleId: l.HarvesterArticleId);

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
        List<string> Genres, string? OriginalLanguage, string? EnrichmentSource, bool MetadataUncertain,
        int? SeasonNumber);

    public sealed record SearchPageDto(int Total, int Page, int PageSize, List<SearchHitDto> Items);

    public sealed record TitleDetailDto(
        int Id, string Title, string? OriginalTitle, string Category, string? Poster,
        int? Year, double? Rating, int? Runtime, string? Overview,
        List<string> Genres, string? OriginalLanguage, string? ImdbId, int? TmdbId,
        bool MetadataUncertain, List<LinkDto> Links, List<EpisodeDto> Episodes);

    public sealed record EpisodeDto(int Id, int SeasonNumber, int EpisodeNumber,
        string? EpisodeName, bool IsFullSeason, List<LinkDto> Links);

    public sealed record LinkDto(int Id, string Url, string Host, string? Quality,
        string? AudioLangs, string? SubLangs, long? SizeBytes,
        string Source, int? HarvesterArticleId);

    public sealed record FacetEntry(string Key, int Count);

    public sealed record FacetsResponse(
        List<FacetEntry> Categories,
        List<FacetEntry> Hosts,
        List<FacetEntry> Qualities,
        List<FacetEntry> Audio);

    public sealed record SendLinksReq(List<int> LinkIds);
    public sealed record SendResultDto(bool Ok, string? Error, List<string> TaskIds, int Submitted);

    public sealed record InventoryFileDto(string Name, long? SizeBytes, DateTimeOffset? ModifiedAt);

    public sealed record InventoryDto(
        bool Ok, string? Error, bool Exists, string FolderPath, int? SeasonNumber,
        List<InventoryFileDto> Files)
    {
        public static InventoryDto Missing(string path, int? season) =>
            new(Ok: true, Error: null, Exists: false, FolderPath: path, SeasonNumber: season, Files: new());

        public static InventoryDto From(DsmListResult listed, int? season)
        {
            if (!listed.Exists)
                return Missing(listed.FolderPath, season);
            var files = listed.Files
                .Where(f => !f.IsDir && !f.Name.StartsWith('.'))
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .Select(f => new InventoryFileDto(f.Name, f.SizeBytes, f.ModifiedAt))
                .ToList();
            return new InventoryDto(
                Ok: true, Error: null, Exists: true,
                FolderPath: listed.FolderPath, SeasonNumber: season, Files: files);
        }
    }

    // Matches "Season 1", "Season 01", "Saison 1", "Saison 01", "S1", "S01",
    // or a folder name that's just the season number — covers the user's
    // existing French folders and the canonical English ones the destination
    // builder produces.
    private static bool MatchesSeasonFolder(string folderName, int seasonNumber)
    {
        if (string.IsNullOrWhiteSpace(folderName)) return false;
        var name = folderName.Trim();
        var n = seasonNumber.ToString();
        var padded = seasonNumber.ToString("D2");
        ReadOnlySpan<string> prefixes = new[] { "Season ", "Saison ", "S" };
        foreach (var p in prefixes)
        {
            if (name.StartsWith(p, StringComparison.OrdinalIgnoreCase))
            {
                var rest = name[p.Length..].TrimStart('0');
                if (rest.Length == 0) rest = "0";
                if (rest == n) return true;
            }
        }
        // Bare number: "1", "01"
        return name == n || name == padded;
    }

    private static string BuildCatalogDestination(
        List<CatalogLinkEntity> links,
        AppSettingsSnapshot s)
    {
        // Mixed-category batch: fall back to the movie root (rare in practice
        // — users send one title at a time).
        var distinctTitles = links.Select(l => l.TitleId).Distinct().Count();
        if (distinctTitles != 1)
            return s.SynologyMovieDestination;

        var first = links[0].Title;
        return MatchPlan(first, s).FullPath;
    }

    private static IReadOnlyList<string> CatalogFoldersToEnsure(
        List<CatalogLinkEntity> links,
        AppSettingsSnapshot s)
    {
        var distinctTitles = links.Select(l => l.TitleId).Distinct().Count();
        if (distinctTitles != 1) return Array.Empty<string>();
        return MatchPlan(links[0].Title, s).ParentSegments;
    }

    private static DsmDestinationBuilder.DestinationPlan MatchPlan(
        CatalogTitleEntity title,
        AppSettingsSnapshot s)
    {
        // CatalogTitles store CategoryName as a string ("Films", "Séries",
        // "Animes", "Other"). Map to our kind-aware path builder.
        return title.CategoryName switch
        {
            var c when string.Equals(c, "Films", StringComparison.OrdinalIgnoreCase) =>
                DsmDestinationBuilder.ForMovie(s.SynologyMovieDestination),
            var c when string.Equals(c, "Animes", StringComparison.OrdinalIgnoreCase) =>
                DsmDestinationBuilder.ForAnime(s.SynologyAnimeDestination, title.TitleName, FirstSeasonNumber(title)),
            var c when string.Equals(c, "Séries", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(c, "Series", StringComparison.OrdinalIgnoreCase) =>
                DsmDestinationBuilder.ForSeries(s.SynologySeriesDestination, title.TitleName, FirstSeasonNumber(title)),
            _ => DsmDestinationBuilder.ForMovie(s.SynologyMovieDestination),
        };
    }

    private static int? FirstSeasonNumber(CatalogTitleEntity title)
    {
        // CatalogTitleEntity.Episodes isn't always loaded; if we have it, use
        // the lowest non-zero season we know about. Otherwise leave null and
        // the path builder will produce <Root>/<Title> (no Season N level).
        if (title.Episodes is not { Count: > 0 }) return null;
        var seasons = title.Episodes
            .Select(e => e.SeasonNumber)
            .Where(n => n > 0)
            .ToList();
        return seasons.Count == 0 ? null : seasons.Min();
    }
}
