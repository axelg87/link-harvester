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
    private readonly PlexClient _plex;
    private readonly ILogger<DiscoveryRefreshService> _log;

    public DiscoveryRefreshService(
        IDbContextFactory<HarvesterDbContext> factory,
        ISettingsService settings,
        TmdbClient tmdb,
        TraktClient trakt,
        LetterboxdPopularScraper letterboxd,
        PlexClient plex,
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

        if (!string.IsNullOrEmpty(snap.TmdbApiKey))
        {
            await RefreshTmdbAsync("tmdb_top_rated_movie", "TMDB Top Rated", "top_rated", false, snap.TmdbApiKey, summary, ct);
            await RefreshTmdbAsync("tmdb_popular_movie", "TMDB Popular", "popular", false, snap.TmdbApiKey, summary, ct);
            await RefreshTmdbAsync("tmdb_top_rated_tv", "TMDB Top Rated TV", "top_rated", true, snap.TmdbApiKey, summary, ct);
            await RefreshTmdbAsync("tmdb_popular_tv", "TMDB Popular TV", "popular", true, snap.TmdbApiKey, summary, ct);
        }

        if (!string.IsNullOrEmpty(snap.TraktClientId))
        {
            await RefreshTraktAsync("trakt_trending_movies", "Trakt Trending", "movies", snap.TraktClientId, summary, ct);
            await RefreshTraktAsync("trakt_trending_shows", "Trakt Trending TV", "shows", snap.TraktClientId, summary, ct);
        }

        await RefreshLetterboxdAsync(summary, ct);

        // Plex Discover — best effort. Public watchlist endpoint may or may
        // not return data with the user's existing token scope; if it
        // doesn't, we drop the source silently rather than fail the run.
        if (!string.IsNullOrEmpty(snap.PlexToken))
        {
            try
            {
                await RefreshPlexAsync(snap.PlexToken, summary, ct);
            }
            catch (Exception ex)
            {
                _log.LogInformation(ex, "plex discover skipped (likely scope issue)");
            }
        }

        // Drop stale rows from sources that didn't refresh this run (older
        // than 48h) so we don't show ghost entries from a defunct source.
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

        _log.LogInformation(
            "discovery refresh complete: tmdb={Tmdb} trakt={Trakt} letterboxd={Lb} plex={Plex} pruned={Pruned}",
            summary.TmdbInserted, summary.TraktInserted, summary.LetterboxdInserted, summary.PlexInserted, summary.Pruned);
        return summary;
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
        if (tmdbId.HasValue && tmdbId.Value > 0)
        {
            var byTmdb = await db.CatalogTitles
                .Where(t => !t.IsHidden && t.TmdbId == tmdbId)
                .Select(t => (int?)t.Id)
                .FirstOrDefaultAsync(ct);
            if (byTmdb.HasValue) return byTmdb;
        }
        if (!string.IsNullOrEmpty(imdbId))
        {
            var byImdb = await db.CatalogTitles
                .Where(t => !t.IsHidden && t.ImdbId == imdbId)
                .Select(t => (int?)t.Id)
                .FirstOrDefaultAsync(ct);
            if (byImdb.HasValue) return byImdb;
        }
        if (!string.IsNullOrWhiteSpace(title))
        {
            var normalized = Normalize(title);
            var byTitle = await db.CatalogTitles
                .Where(t => !t.IsHidden && t.NormalizedTitle == normalized)
                .Select(t => new { t.Id, Year = t.Metadata != null ? t.Metadata.Year : null })
                .ToListAsync(ct);
            // Prefer matching year within ±1 if we have one, else any match.
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

    private static string Normalize(string value)
        => new(value.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

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
