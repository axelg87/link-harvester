using System.Text.Json;
using System.Threading.Channels;
using LinkHarvester.Core;
using LinkHarvester.Persistence;
using LinkHarvester.Persistence.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LinkHarvester.Enrichment;

/// <summary>
/// Background service that fills <see cref="CatalogTitleMetadataEntity"/> rows
/// from TMDB. Runs continuously while TMDB is configured + enrichment enabled.
///
/// Pipeline:
///   1. Pick the next batch of titles missing metadata or in 'failed' state with attempts &lt; 5.
///   2. Fan out to N workers (capped at SettingsService.TmdbEnrichmentConcurrency, default 4).
///   3. Each worker hits TMDB, writes the result back.
///   4. Global token-bucket keeps us under TMDB's 40 req/10s ceiling regardless of N.
///
/// Restarts cleanly: state is persisted, no in-memory queue lost on restart.
/// </summary>
public sealed class TmdbEnricherService : BackgroundService
{
    private readonly IDbContextFactory<HarvesterDbContext> _factory;
    private readonly ISettingsService _settings;
    private readonly TmdbClient _tmdb;
    private readonly TmdbStatusTracker _tracker;
    private readonly ILogger<TmdbEnricherService> _log;

    public TmdbEnricherService(IDbContextFactory<HarvesterDbContext> factory,
                                ISettingsService settings,
                                TmdbClient tmdb,
                                TmdbStatusTracker tracker,
                                ILogger<TmdbEnricherService> log)
    {
        _factory = factory;
        _settings = settings;
        _tmdb = tmdb;
        _tracker = tracker;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        var bucket = new TokenBucket(ratePerSecond: 3.5, burst: 8);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var snap = _settings.Current;
                if (string.IsNullOrEmpty(snap.TmdbApiKey) || !snap.TmdbEnrichmentEnabled)
                {
                    await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
                    continue;
                }

                var batch = await LoadNextBatchAsync(500, stoppingToken);
                if (batch.Count == 0)
                {
                    _tracker.SetIdle();
                    await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
                    continue;
                }

                _tracker.SetRunning();

                var concurrency = Math.Clamp(snap.TmdbEnrichmentConcurrency, 1, 8);
                var channel = Channel.CreateBounded<EnrichWorkItem>(new BoundedChannelOptions(concurrency * 2)
                {
                    FullMode = BoundedChannelFullMode.Wait
                });

                var apiKey = snap.TmdbApiKey;
                var workers = Enumerable.Range(0, concurrency)
                    .Select(_ => Task.Run(() => RunWorkerAsync(channel.Reader, apiKey, bucket, stoppingToken), stoppingToken))
                    .ToArray();

                foreach (var item in batch)
                {
                    if (stoppingToken.IsCancellationRequested) break;
                    await channel.Writer.WriteAsync(item, stoppingToken);
                }
                channel.Writer.Complete();
                await Task.WhenAll(workers);
            }
            catch (OperationCanceledException) { /* shutdown */ }
            catch (Exception ex)
            {
                _log.LogError(ex, "enricher loop crashed");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
    }

    private async Task<List<EnrichWorkItem>> LoadNextBatchAsync(int limit, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        // Titles that have no metadata row yet, OR a pending/failed row with < 5 attempts.
        var query = from t in db.CatalogTitles
                    join m in db.CatalogTitleMetadata on t.Id equals m.TitleId into mg
                    from m in mg.DefaultIfEmpty()
                    where m == null
                       || (m.EnrichmentSource == "pending" && m.Attempts < 5)
                       || (m.EnrichmentSource == "failed" && m.Attempts < 5)
                    orderby t.Id
                    select new EnrichWorkItem
                    {
                        TitleId = t.Id,
                        TmdbId = t.TmdbId,
                        ImdbId = t.ImdbId,
                        Category = t.CategoryName,
                        TitleName = t.TitleName,
                        Attempts = m == null ? 0 : m.Attempts
                    };
        return await query.Take(limit).ToListAsync(ct);
    }

    private async Task RunWorkerAsync(ChannelReader<EnrichWorkItem> reader, string apiKey, TokenBucket bucket, CancellationToken ct)
    {
        while (await reader.WaitToReadAsync(ct))
        {
            while (reader.TryRead(out var item))
            {
                await bucket.WaitAsync(ct);
                try
                {
                    await ProcessOneAsync(item, apiKey, ct);
                }
                catch (TmdbRateLimitException rl)
                {
                    _log.LogWarning("hit TMDB rate limit; sleeping {Delay}", rl.RetryAfter);
                    await Task.Delay(rl.RetryAfter, ct);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "enrich titleId {Id} failed", item.TitleId);
                    await RecordFailureAsync(item, ex.Message, ct);
                }
            }
        }
    }

    private async Task ProcessOneAsync(EnrichWorkItem item, string apiKey, CancellationToken ct)
    {
        var isSeries = !string.Equals(item.Category, "Films", StringComparison.OrdinalIgnoreCase)
                       && !string.Equals(item.Category, "Movie", StringComparison.OrdinalIgnoreCase);

        TmdbDetails? details = null;
        string source = "tmdb_direct";

        if (item.TmdbId is int tmdbId && tmdbId > 0)
        {
            details = await _tmdb.FetchByTmdbIdAsync(tmdbId, isSeries, apiKey, ct);
            // If category guess was wrong (e.g. Films but actually a TV show), try the other endpoint.
            if (details is null)
            {
                details = await _tmdb.FetchByTmdbIdAsync(tmdbId, !isSeries, apiKey, ct);
                if (details is not null) isSeries = !isSeries;
            }
        }

        if (details is null && !string.IsNullOrEmpty(item.ImdbId))
        {
            var find = await _tmdb.FindByImdbAsync(item.ImdbId!, apiKey, ct);
            var entry = (isSeries ? find?.TvResults : find?.MovieResults)?.FirstOrDefault()
                        ?? find?.MovieResults?.FirstOrDefault()
                        ?? find?.TvResults?.FirstOrDefault();
            if (entry is not null)
            {
                details = await _tmdb.FetchByTmdbIdAsync(entry.Id, isSeries, apiKey, ct);
                source = "tmdb_imdb_lookup";
            }
        }

        if (details is null)
        {
            await RecordFailureAsync(item, "no_tmdb_match", ct);
            return;
        }

        await using var db = await _factory.CreateDbContextAsync(ct);
        var meta = await db.CatalogTitleMetadata.FirstOrDefaultAsync(m => m.TitleId == item.TitleId, ct)
            ?? new CatalogTitleMetadataEntity { TitleId = item.TitleId };

        meta.TmdbId = details.TmdbId;
        meta.ImdbId = details.ImdbId ?? item.ImdbId;
        meta.ReleaseDate = details.ReleaseDate;
        meta.Year = details.Year;
        meta.Runtime = details.Runtime;
        meta.VoteAverage = details.VoteAverage;
        meta.VoteCount = details.VoteCount;
        meta.Popularity = details.Popularity;
        meta.GenresJson = JsonSerializer.Serialize(details.Genres);
        meta.OriginalLanguage = details.OriginalLanguage;
        meta.Overview = details.Overview;
        meta.Status = details.Status;
        meta.EnrichmentSource = source;
        meta.LastEnrichedAt = DateTimeOffset.UtcNow;
        meta.Attempts = item.Attempts + 1;
        meta.LastError = null;

        if (meta.Id == 0)
            db.CatalogTitleMetadata.Add(meta);
        await db.SaveChangesAsync(ct);
        _tracker.IncrementEnriched();
    }

    private async Task RecordFailureAsync(EnrichWorkItem item, string error, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var meta = await db.CatalogTitleMetadata.FirstOrDefaultAsync(m => m.TitleId == item.TitleId, ct)
            ?? new CatalogTitleMetadataEntity { TitleId = item.TitleId };
        meta.Attempts = item.Attempts + 1;
        meta.EnrichmentSource = meta.Attempts >= 5 ? "failed" : "pending";
        meta.LastError = error.Length > 480 ? error[..480] : error;
        meta.LastEnrichedAt = DateTimeOffset.UtcNow;
        if (meta.Id == 0) db.CatalogTitleMetadata.Add(meta);
        await db.SaveChangesAsync(ct);
        _tracker.IncrementFailed();
    }

    private sealed class EnrichWorkItem
    {
        public int TitleId { get; init; }
        public int? TmdbId { get; init; }
        public string? ImdbId { get; init; }
        public string Category { get; init; } = "";
        public string TitleName { get; init; } = "";
        public int Attempts { get; init; }
    }
}

/// <summary>
/// Per-process tracker for enrichment progress, exposed via the API for UI display.
/// </summary>
public sealed class TmdbStatusTracker
{
    private long _enriched;
    private long _failed;
    private string _state = "idle";

    public string State => _state;
    public long Enriched => Interlocked.Read(ref _enriched);
    public long Failed => Interlocked.Read(ref _failed);

    public void SetRunning() => _state = "running";
    public void SetIdle() => _state = "idle";
    public void IncrementEnriched() => Interlocked.Increment(ref _enriched);
    public void IncrementFailed() => Interlocked.Increment(ref _failed);
}

/// <summary>
/// Simple token bucket. Calls to <see cref="WaitAsync"/> block until a token
/// is available; tokens accrue at <paramref name="ratePerSecond"/>, capped at
/// <paramref name="burst"/>.
/// </summary>
internal sealed class TokenBucket
{
    private readonly double _rate;
    private readonly double _burst;
    private double _tokens;
    private DateTime _last = DateTime.UtcNow;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public TokenBucket(double ratePerSecond, double burst)
    {
        _rate = ratePerSecond;
        _burst = burst;
        _tokens = burst;
    }

    public async Task WaitAsync(CancellationToken ct)
    {
        while (true)
        {
            await _gate.WaitAsync(ct);
            try
            {
                var now = DateTime.UtcNow;
                var elapsed = (now - _last).TotalSeconds;
                _tokens = Math.Min(_burst, _tokens + elapsed * _rate);
                _last = now;
                if (_tokens >= 1)
                {
                    _tokens -= 1;
                    return;
                }
            }
            finally { _gate.Release(); }
            await Task.Delay(120, ct);
        }
    }
}
