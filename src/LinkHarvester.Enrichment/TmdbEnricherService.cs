using System.Text.Json;
using System.Threading.Channels;
using LinkHarvester.Core;
using LinkHarvester.Persistence;
using LinkHarvester.Persistence.Cards;
using LinkHarvester.Persistence.Catalog;
using Microsoft.Data.Sqlite;
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
///   4. Per-batch token-bucket keeps us under TMDB's 40 req/10s ceiling regardless of N.
///
/// Pause/resume contract:
///   - Each batch runs under its own CancellationTokenSource linked to the
///     hosting <c>stoppingToken</c>. The CTS is cancelled either when the host
///     stops or when settings flip to <c>tmdbEnrichmentEnabled = false</c>
///     (via <see cref="ISettingsService.Changed"/>). That guarantees an
///     in-flight batch tears down promptly instead of silently completing in
///     the background, which previously left workers stuck on a stale channel
///     and a stale token bucket — symptom: state=running but no TMDB calls.
///
/// Restarts cleanly: state is persisted, no in-memory queue lost on restart.
/// </summary>
public sealed class TmdbEnricherService : BackgroundService
{
    private static readonly TimeSpan GateRecheckInterval = TimeSpan.FromSeconds(2);

    private readonly IDbContextFactory<HarvesterDbContext> _factory;
    private readonly ISettingsService _settings;
    private readonly TmdbClient _tmdb;
    private readonly TmdbStatusTracker _tracker;
    private readonly ICatalogIngestionStatus _ingestionStatus;
    private readonly ICardKeeper _cards;
    private readonly ILogger<TmdbEnricherService> _log;

    /// <summary>
    /// Tracks the in-flight batch so that a settings flip to disabled (via
    /// the Changed event) can cancel it immediately, rather than waiting for
    /// the batch to drain naturally.
    /// </summary>
    private CancellationTokenSource? _currentBatchCts;

    public TmdbEnricherService(IDbContextFactory<HarvesterDbContext> factory,
                                ISettingsService settings,
                                TmdbClient tmdb,
                                TmdbStatusTracker tracker,
                                ICatalogIngestionStatus ingestionStatus,
                                ICardKeeper cards,
                                ILogger<TmdbEnricherService> log)
    {
        _factory = factory;
        _settings = settings;
        _tmdb = tmdb;
        _tracker = tracker;
        _ingestionStatus = ingestionStatus;
        _cards = cards;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        void OnSettingsChanged()
        {
            // If enrichment was just disabled (or the API key cleared),
            // cancel any in-flight batch so the loop re-evaluates promptly.
            var s = _settings.Current;
            if (!s.TmdbEnrichmentEnabled || string.IsNullOrEmpty(s.TmdbApiKey))
            {
                try { Volatile.Read(ref _currentBatchCts)?.Cancel(); }
                catch (ObjectDisposedException) { /* race with batch teardown — fine */ }
            }
        }

        _settings.Changed += OnSettingsChanged;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await RunOneIterationAsync(stoppingToken);
            }
        }
        finally
        {
            _settings.Changed -= OnSettingsChanged;
            Volatile.Write(ref _currentBatchCts, null);
        }
    }

    private async Task RunOneIterationAsync(CancellationToken stoppingToken)
    {
        // One CTS per iteration, linked to the host stop token. Disposed in
        // finally — guarantees workers from this batch can never linger into
        // the next iteration even if cancellation races with batch completion.
        using var batchCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        Volatile.Write(ref _currentBatchCts, batchCts);

        try
        {
            var snap = _settings.Current;
            if (string.IsNullOrEmpty(snap.TmdbApiKey) || !snap.TmdbEnrichmentEnabled)
            {
                _tracker.SetIdle();
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
                return;
            }

            // BUG-2 prevention: while a catalog ingestion is in flight, its
            // 25k-record write transactions hold the SQLite writer for
            // 10–30 s at a time and contend with our per-title UPDATEs.
            // Rather than racing for the lock and producing false 'database
            // is locked' failures, stand down until the ingestor releases.
            // Poll cheaply (a memory read) every GateRecheckInterval so the
            // enricher resumes promptly when the import finishes.
            if (_ingestionStatus.IsRunning)
            {
                _tracker.SetIdle();
                _log.LogInformation("enricher pausing batch: catalog ingestion in progress");
                while (!stoppingToken.IsCancellationRequested && _ingestionStatus.IsRunning)
                {
                    await Task.Delay(GateRecheckInterval, stoppingToken);
                }
                if (stoppingToken.IsCancellationRequested) return;
                _log.LogInformation("enricher resuming: ingestion no longer running");
            }

            List<EnrichWorkItem> batch;
            try
            {
                batch = await LoadNextBatchAsync(500, batchCts.Token);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                // Settings flipped while we were loading — short-circuit.
                return;
            }

            if (batch.Count == 0)
            {
                _tracker.SetIdle();
                await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
                return;
            }

            _tracker.SetRunning();
            _log.LogInformation("enricher batch loaded: {Count} items", batch.Count);

            var startedAt = DateTimeOffset.UtcNow;
            var startEnriched = _tracker.Enriched;
            var startFailed = _tracker.Failed;

            // Fresh bucket per batch — guarantees burst capacity is reset on
            // resume, never inheriting stale token-debt or accrual time from
            // a previous, possibly-aborted, batch.
            var bucket = new TokenBucket(ratePerSecond: 3.5, burst: 8);

            var concurrency = Math.Clamp(snap.TmdbEnrichmentConcurrency, 1, 8);
            var channel = Channel.CreateBounded<EnrichWorkItem>(new BoundedChannelOptions(concurrency * 2)
            {
                FullMode = BoundedChannelFullMode.Wait
            });

            var apiKey = snap.TmdbApiKey;
            var workerCt = batchCts.Token;
            var workers = Enumerable.Range(0, concurrency)
                .Select(_ => Task.Run(() => RunWorkerAsync(channel.Reader, apiKey, bucket, workerCt), workerCt))
                .ToArray();

            try
            {
                foreach (var item in batch)
                {
                    if (batchCts.IsCancellationRequested) break;
                    await channel.Writer.WriteAsync(item, batchCts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                // Batch was cancelled mid-write — fall through to drain workers.
            }
            finally
            {
                channel.Writer.TryComplete();
            }

            try
            {
                await Task.WhenAll(workers);
            }
            catch (OperationCanceledException)
            {
                // Expected when the batch was cancelled by a settings flip.
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "enricher worker faulted");
            }

            var elapsed = DateTimeOffset.UtcNow - startedAt;
            var enriched = _tracker.Enriched - startEnriched;
            var failed = _tracker.Failed - startFailed;
            if (batchCts.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
            {
                _log.LogInformation(
                    "enricher batch aborted: enriched={Enriched} failed={Failed} elapsedMs={Elapsed}",
                    enriched, failed, (long)elapsed.TotalMilliseconds);
            }
            else
            {
                _log.LogInformation(
                    "enricher batch complete: enriched={Enriched} failed={Failed} elapsedMs={Elapsed}",
                    enriched, failed, (long)elapsed.TotalMilliseconds);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutting down.
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "enricher loop crashed");
            try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
            catch (OperationCanceledException) { /* shutdown */ }
        }
        finally
        {
            Volatile.Write(ref _currentBatchCts, null);
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
                        Year = m == null ? null : m.Year,
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
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // Batch was cancelled (host stop or settings flip). Do NOT
                    // record this as a per-title failure — that would falsely
                    // burn an attempt and eventually mark the title 'failed'.
                    throw;
                }
                catch (TmdbRateLimitException rl)
                {
                    _log.LogWarning("hit TMDB rate limit; sleeping {Delay}", rl.RetryAfter);
                    try { await Task.Delay(rl.RetryAfter, ct); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                }
                catch (Exception ex) when (IsTransientSqliteContention(ex))
                {
                    // BUG-2: SQLite "database is locked" / busy-timeout while
                    // the catalog ingestor holds the writer. Treat as transient:
                    // do NOT increment Attempts and do NOT set LastError. Leave
                    // the title untouched so the next batch picks it up after
                    // the ingestor releases. Without this guard, 5 such races
                    // marked the title permanently 'failed'.
                    _log.LogWarning(ex, "transient SQLite contention on titleId {Id}; will retry next batch", item.TitleId);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "enrich titleId {Id} failed", item.TitleId);
                    try { await RecordFailureAsync(item, ex.Message, ct); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception writeEx)
                    {
                        _log.LogWarning(writeEx, "could not record failure for titleId {Id}", item.TitleId);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Returns true when an exception (or any of its inner exceptions) looks
    /// like SQLite write contention — error code 5 (SQLITE_BUSY), 6
    /// (SQLITE_LOCKED), or a message containing "database is locked"/"busy"
    /// /"timeout". The message-pattern fallback catches cases where the
    /// exception is wrapped (e.g. by EF Core) with the SqliteException buried
    /// in the chain.
    /// </summary>
    internal static bool IsTransientSqliteContention(Exception ex)
    {
        for (var e = (Exception?)ex; e is not null; e = e.InnerException)
        {
            if (e is SqliteException sx)
            {
                // 5 = SQLITE_BUSY, 6 = SQLITE_LOCKED.
                if (sx.SqliteErrorCode == 5 || sx.SqliteErrorCode == 6) return true;
            }
            var m = e.Message;
            if (!string.IsNullOrEmpty(m) &&
                (m.Contains("database is locked", StringComparison.OrdinalIgnoreCase)
                 || m.Contains("database is busy", StringComparison.OrdinalIgnoreCase)
                 || m.Contains("SQLite Error 5", StringComparison.OrdinalIgnoreCase)
                 || m.Contains("SQLite Error 6", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }
        return false;
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
            var match = await _tmdb.SearchByTitleAsync(item.TitleName, item.Year, isSeries, apiKey, ct);
            if (match is not null)
            {
                details = await _tmdb.FetchByTmdbIdAsync(match.TmdbId, isSeries, apiKey, ct);
                source = match.Uncertain ? "tmdb_search_uncertain" : "tmdb_search";
            }
            if (details is null)
            {
                await RecordFailureAsync(item, "no_tmdb_match", ct);
                return;
            }
        }

        await using var db = await _factory.CreateDbContextAsync(ct);
        var title = await db.CatalogTitles.FirstOrDefaultAsync(t => t.Id == item.TitleId, ct);
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
        meta.LastAirDate = details.LastAirDate;
        meta.NextEpisodeAirDate = details.NextEpisodeAirDate;
        meta.NextEpisodeCode = details.NextEpisodeCode;
        meta.EnrichmentSource = source;
        meta.MetadataUncertain = string.Equals(source, "tmdb_search_uncertain", StringComparison.Ordinal);
        meta.LastEnrichedAt = DateTimeOffset.UtcNow;
        meta.Attempts = item.Attempts + 1;
        meta.LastError = null;

        if (meta.Id == 0)
            db.CatalogTitleMetadata.Add(meta);
        if (title is not null && !string.IsNullOrWhiteSpace(details.PosterUrl))
        {
            title.TitlePoster = details.PosterUrl;
            title.TmdbId ??= details.TmdbId;
            title.ImdbId ??= details.ImdbId;
        }
        await db.SaveChangesAsync(ct);
        // Successful enrichment changed Year / Rating / Genres / Overview /
        // Popularity / Poster — every field the catalog card surfaces.
        await _cards.UpsertCatalogCardAsync(db, item.TitleId, ct);
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
        // EnrichmentSource flip ("pending"/"failed") shows in the
        // hasMetadata facet, so the card still needs a refresh.
        await _cards.UpsertCatalogCardAsync(db, item.TitleId, ct);
        _tracker.IncrementFailed();
    }

    private sealed class EnrichWorkItem
    {
        public int TitleId { get; init; }
        public int? TmdbId { get; init; }
        public string? ImdbId { get; init; }
        public string Category { get; init; } = "";
        public string TitleName { get; init; } = "";
        public int? Year { get; init; }
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
