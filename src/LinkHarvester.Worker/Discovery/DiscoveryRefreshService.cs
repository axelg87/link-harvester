using LinkHarvester.Core;
using LinkHarvester.Enrichment;
using LinkHarvester.Persistence;
using LinkHarvester.Persistence.Catalog;
using LinkHarvester.Synology;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LinkHarvester.Worker.Discovery;

/// <summary>
/// Nightly worker that populates <see cref="DiscoveryEntryEntity"/> from
/// external popularity sources, joined against the local catalog so we only
/// keep rows the user can actually download. Sources are best-effort —
/// missing config (no TMDB key, no Trakt id, etc.) silently skips that
/// source rather than failing the whole run.
///
/// Schedule: 03:00 local on startup, then every 24h. Manual refresh via
/// <see cref="RunOnceAsync"/> for the /api/discover/refresh endpoint.
/// </summary>
public sealed class DiscoveryRefreshService : BackgroundService
{
    private static readonly TimeSpan RefreshPeriod = TimeSpan.FromHours(24);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);

    private readonly IDbContextFactory<HarvesterDbContext> _factory;
    private readonly ISettingsService _settings;
    private readonly TmdbClient _tmdb;
    private readonly TraktClient _trakt;
    private readonly LetterboxdPopularScraper _letterboxd;
    private readonly IPlexClient _plex;
    private readonly ILogger<DiscoveryRefreshService> _log;

    public DiscoveryRefreshService(
        IDbContextFactory<HarvesterDbContext> factory,
        ISettingsService settings,
        TmdbClient tmdb,
        TraktClient trakt,
        LetterboxdPopularScraper letterboxd,
        IPlexClient plex,
        ILogger<DiscoveryRefreshService> log)
    {
        _factory = factory;
        _settings = settings;
        _tmdb = tmdb;
        _trakt = trakt;
        _letterboxd = letterboxd;
        _plex = plex;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(StartupDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _log.LogError(ex, "discovery refresh failed");
            }
            try { await Task.Delay(RefreshPeriod, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    public async Task<DiscoveryRefreshSummary> RunOnceAsync(CancellationToken ct)
    {
        var snap = _settings.Current;
        var summary = new DiscoveryRefreshSummary();
        var totalSw = System.Diagnostics.Stopwatch.StartNew();
        _log.LogInformation(
            "discovery refresh started. tmdb={TmdbSet} trakt={TraktSet} plex={PlexSet}",
            !string.IsNullOrEmpty(snap.TmdbApiKey),
            !string.IsNullOrEmpty(snap.TraktClientId),
            !string.IsNullOrEmpty(snap.PlexToken));

        if (!string.IsNullOrEmpty(snap.TmdbApiKey))
        {
            await RunSourceAsync("tmdb_top_rated_movie", () => RefreshTmdbAsync("tmdb_top_rated_movie", "TMDB Top Rated", "top_rated", false, snap.TmdbApiKey, summary, ct));
            await RunSourceAsync("tmdb_popular_movie", () => RefreshTmdbAsync("tmdb_popular_movie", "TMDB Popular", "popular", false, snap.TmdbApiKey, summary, ct));
            await RunSourceAsync("tmdb_top_rated_tv", () => RefreshTmdbAsync("tmdb_top_rated_tv", "TMDB Top Rated TV", "top_rated", true, snap.TmdbApiKey, summary, ct));
            await RunSourceAsync("tmdb_popular_tv", () => RefreshTmdbAsync("tmdb_popular_tv", "TMDB Popular TV", "popular", true, snap.TmdbApiKey, summary, ct));
        }
        else
        {
            _log.LogInformation("discovery source skipped: tmdb (no api key)");
        }

        if (!string.IsNullOrEmpty(snap.TraktClientId))
        {
            await RunSourceAsync("trakt_trending_movies", () => RefreshTraktAsync("trakt_trending_movies", "Trakt Trending", "movies", snap.TraktClientId, summary, ct));
            await RunSourceAsync("trakt_trending_shows", () => RefreshTraktAsync("trakt_trending_shows", "Trakt Trending TV", "shows", snap.TraktClientId, summary, ct));
        }
        else
        {
            _log.LogInformation("discovery source skipped: trakt (no client id)");
        }

        await RunSourceAsync("letterboxd_popular_week", () => RefreshLetterboxdAsync(summary, ct));

        if (!string.IsNullOrEmpty(snap.PlexToken))
        {
            try { await RunSourceAsync("plex_most_watchlisted", () => RefreshPlexAsync(snap.PlexToken, summary, ct)); }
            catch (Exception ex) { _log.LogInformation(ex, "plex discover skipped (likely scope issue)"); }
        }
        else
        {
            _log.LogInformation("discovery source skipped: plex (no token)");
        }

        var staleCutoff = DateTimeOffset.UtcNow - TimeSpan.FromHours(48);
        await using (var db = await _factory.CreateDbContextAsync(ct))
        {
            var stale = await db.DiscoveryEntries
                .Where(d => d.FetchedAt < staleCutoff)
                .ToListAsync(ct);
            if (stale.Count > 0)
            {
                db.DiscoveryEntries.RemoveRange(stale);
                await db.SaveChangesAsync(ct);
                summary.Pruned += stale.Count;
            }
        }

        totalSw.Stop();
        _log.LogInformation(
            "discovery refresh complete in {Elapsed}: tmdb={Tmdb} trakt={Trakt} letterboxd={Lb} plex={Plex} pruned={Pruned}",
            totalSw.Elapsed, summary.TmdbInserted, summary.TraktInserted, summary.LetterboxdInserted, summary.PlexInserted, summary.Pruned);
        return summary;
    }

    /// <summary>Wraps each per-source refresh with a start/end log + duration
    /// so a stalled refresh shows exactly which source it died inside.</summary>
    private async Task RunSourceAsync(string source, Func<Task> body)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _log.LogInformation("discovery source {Source} starting", source);
        try
        {
            await body();
            sw.Stop();
            _log.LogInformation("discovery source {Source} finished in {Elapsed}", source, sw.Elapsed);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _log.LogWarning(ex, "discovery source {Source} threw after {Elapsed}", source, sw.Elapsed);
        }
    }

    private async Task RefreshTmdbAsync(string source, string label, string list, bool isSeries, string apiKey,
        DiscoveryRefreshSummary summary, CancellationToken ct)
    {
        try
        {
            // Pull 3 pages = ~60 titles. TMDB caps each page at 20.
            var all = new List<TmdbPopularEntry>();
            for (var page = 1; page <= 3; page++)
            {
                all.AddRange(await _tmdb.FetchPopularListAsync(list, isSeries, page, apiKey, ct));
            }
            if (all.Count == 0) return;

            await using var db = await _factory.CreateDbContextAsync(ct);
            await ClearSourceAsync(db, source, ct);

            var rank = 0;
            foreach (var entry in all)
            {
                rank++;
                var titleId = await ResolveCatalogTitleAsync(db, tmdbId: entry.Id, imdbId: null,
                    title: entry.ResolvedTitle, year: ParseYear(entry.FirstAirDate ?? entry.ReleaseDate), ct);
                if (titleId is null) continue;

                db.DiscoveryEntries.Add(new DiscoveryEntryEntity
                {
                    CatalogTitleId = titleId.Value,
                    Source = source,
                    Rank = rank,
                    Reason = $"{label} (#{rank})",
                    FetchedAt = DateTimeOffset.UtcNow
                });
                summary.TmdbInserted++;
            }
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "tmdb source {Source} refresh failed", source);
        }
    }

    private async Task RefreshTraktAsync(string source, string label, string kind, string clientId,
        DiscoveryRefreshSummary summary, CancellationToken ct)
    {
        try
        {
            var entries = await _trakt.FetchTrendingAsync(kind, clientId, limit: 60, ct);
            if (entries.Count == 0) return;

            await using var db = await _factory.CreateDbContextAsync(ct);
            await ClearSourceAsync(db, source, ct);

            var rank = 0;
            foreach (var entry in entries)
            {
                rank++;
                var item = entry.Item;
                if (item?.Ids is null) continue;
                var titleId = await ResolveCatalogTitleAsync(db, item.Ids.Tmdb, item.Ids.Imdb,
                    item.Title ?? string.Empty, item.Year, ct);
                if (titleId is null) continue;

                db.DiscoveryEntries.Add(new DiscoveryEntryEntity
                {
                    CatalogTitleId = titleId.Value,
                    Source = source,
                    Rank = rank,
                    Reason = $"{label} (#{rank}, {entry.Watchers} watching)",
                    FetchedAt = DateTimeOffset.UtcNow
                });
                summary.TraktInserted++;
            }
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "trakt source {Source} refresh failed", source);
        }
    }

    private async Task RefreshLetterboxdAsync(DiscoveryRefreshSummary summary, CancellationToken ct)
    {
        try
        {
            var entries = await _letterboxd.FetchAsync(limit: 72, ct);
            if (entries.Count == 0) return;

            await using var db = await _factory.CreateDbContextAsync(ct);
            await ClearSourceAsync(db, "letterboxd_popular_week", ct);

            foreach (var entry in entries)
            {
                var titleId = await ResolveCatalogTitleAsync(db, entry.TmdbId, null, entry.Title, entry.Year, ct);
                if (titleId is null) continue;
                db.DiscoveryEntries.Add(new DiscoveryEntryEntity
                {
                    CatalogTitleId = titleId.Value,
                    Source = "letterboxd_popular_week",
                    Rank = entry.Rank,
                    Reason = $"Letterboxd Popular This Week (#{entry.Rank})",
                    FetchedAt = DateTimeOffset.UtcNow
                });
                summary.LetterboxdInserted++;
            }
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "letterboxd refresh failed");
        }
    }

    private async Task RefreshPlexAsync(string plexToken, DiscoveryRefreshSummary summary, CancellationToken ct)
    {
        var entries = await _plex.FetchDiscoverWatchlistAsync(plexToken, ct);
        if (entries.Count == 0) return;

        await using var db = await _factory.CreateDbContextAsync(ct);
        await ClearSourceAsync(db, "plex_most_watchlisted", ct);

        var rank = 0;
        foreach (var entry in entries)
        {
            rank++;
            var titleId = await ResolveCatalogTitleAsync(db, entry.TmdbId, entry.ImdbId, entry.Title, entry.Year, ct);
            if (titleId is null) continue;
            db.DiscoveryEntries.Add(new DiscoveryEntryEntity
            {
                CatalogTitleId = titleId.Value,
                Source = "plex_most_watchlisted",
                Rank = rank,
                Reason = $"Plex Most Watchlisted (#{rank})",
                FetchedAt = DateTimeOffset.UtcNow
            });
            summary.PlexInserted++;
        }
        await db.SaveChangesAsync(ct);
    }

    private static async Task ClearSourceAsync(HarvesterDbContext db, string source, CancellationToken ct)
    {
        var existing = await db.DiscoveryEntries.Where(d => d.Source == source).ToListAsync(ct);
        if (existing.Count > 0)
        {
            db.DiscoveryEntries.RemoveRange(existing);
            await db.SaveChangesAsync(ct);
        }
    }

    private static async Task<int?> ResolveCatalogTitleAsync(
        HarvesterDbContext db, int? tmdbId, string? imdbId, string title, int? year, CancellationToken ct)
    {
        // TMDB id can live on either CatalogTitleEntity.TmdbId (set by the
        // Hydracker ingestor when the dump included it) OR on
        // CatalogTitleMetadataEntity.TmdbId (set by TmdbEnricherService for
        // titles enriched after ingestion). Most enriched titles only have
        // it on Metadata — that's why every discovery refresh returned 0
        // matches before this lookup checked both places.
        if (tmdbId.HasValue && tmdbId.Value > 0)
        {
            var byTmdb = await db.CatalogTitles
                .Where(t => !t.IsHidden
                         && (t.TmdbId == tmdbId
                             || (t.Metadata != null && t.Metadata.TmdbId == tmdbId)))
                .Select(t => (int?)t.Id)
                .FirstOrDefaultAsync(ct);
            if (byTmdb.HasValue) return byTmdb;
        }
        if (!string.IsNullOrEmpty(imdbId))
        {
            var byImdb = await db.CatalogTitles
                .Where(t => !t.IsHidden
                         && (t.ImdbId == imdbId
                             || (t.Metadata != null && t.Metadata.ImdbId == imdbId)))
                .Select(t => (int?)t.Id)
                .FirstOrDefaultAsync(ct);
            if (byImdb.HasValue) return byImdb;
        }
        if (!string.IsNullOrWhiteSpace(title))
        {
            // CatalogTitle.NormalizedTitle is produced by Core.TitleNormalizer:
            // lower-cased, diacritics stripped, non-alphanumerics collapsed
            // to single spaces. Our local Normalize() must produce the
            // same shape or this branch never matches.
            var normalized = Normalize(title);
            var byTitle = await db.CatalogTitles
                .Where(t => !t.IsHidden && t.NormalizedTitle == normalized)
                .Select(t => new { t.Id, Year = t.Metadata != null ? t.Metadata.Year : null })
                .ToListAsync(ct);
            if (year.HasValue)
            {
                var withYear = byTitle.FirstOrDefault(b => b.Year.HasValue && Math.Abs(b.Year.Value - year.Value) <= 1);
                if (withYear is not null) return withYear.Id;
            }
            var any = byTitle.FirstOrDefault();
            if (any is not null) return any.Id;
        }
        return null;
    }

    /// <summary>
    /// Matches the shape produced by <c>Core.TitleNormalizer</c> — lowercase,
    /// diacritics stripped, non-alphanumerics collapsed to single spaces,
    /// trimmed. The previous version dropped spaces entirely, which made
    /// every normalised-title comparison miss.
    /// </summary>
    private static string Normalize(string value)
    {
        var lowered = value.ToLowerInvariant();
        var stripped = StripDiacritics(lowered);
        var sb = new System.Text.StringBuilder(stripped.Length);
        var lastWasSpace = false;
        foreach (var c in stripped)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(c);
                lastWasSpace = false;
            }
            else if (!lastWasSpace && sb.Length > 0)
            {
                sb.Append(' ');
                lastWasSpace = true;
            }
        }
        return sb.ToString().TrimEnd();
    }

    private static string StripDiacritics(string s)
    {
        var normalized = s.Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        }
        return sb.ToString().Normalize(System.Text.NormalizationForm.FormC);
    }

    private static int? ParseYear(string? date)
    {
        if (string.IsNullOrEmpty(date) || date.Length < 4) return null;
        return int.TryParse(date[..4], out var y) ? y : null;
    }
}

public sealed class DiscoveryRefreshSummary
{
    public int TmdbInserted { get; set; }
    public int TraktInserted { get; set; }
    public int LetterboxdInserted { get; set; }
    public int PlexInserted { get; set; }
    public int Pruned { get; set; }
}
