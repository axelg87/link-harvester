using LinkHarvester.Core;
using LinkHarvester.Persistence;
using LinkHarvester.Persistence.Cards;
using LinkHarvester.Persistence.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LinkHarvester.Enrichment.Tests;

/// <summary>
/// Regression coverage for KNOWN_BUGS.md BUG-1: the TMDB enricher must
/// resume processing after an enabled→disabled→enabled toggle of the
/// <c>tmdbEnrichmentEnabled</c> setting, without requiring a process restart.
/// </summary>
public class TmdbEnricherServiceTests : IAsyncLifetime
{
    private string _dbPath = "";
    private string _connStr = "";
    private TestDbContextFactory _factory = null!;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"lh-enricher-test-{Guid.NewGuid():N}.db");
        _connStr = $"Data Source={_dbPath}";
        _factory = new TestDbContextFactory(_connStr);
        await CatalogSeeder.SeedTitlesAsync(_factory, count: 200);
    }

    public Task DisposeAsync()
    {
        // Force EF to release the file before deletion. SQLite holds the
        // file via its connection pool; a final dispose-cycle is enough.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* best effort */ }
        foreach (var sidecar in new[] { _dbPath + "-wal", _dbPath + "-shm" })
        {
            try { if (File.Exists(sidecar)) File.Delete(sidecar); } catch { /* best effort */ }
        }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Resumes_processing_after_disable_and_reenable()
    {
        var settings = new FakeSettingsService();
        var tracker = new TmdbStatusTracker();
        var handler = new FakeTmdbHandler(_ => TimeSpan.FromMilliseconds(40));
        var http = new HttpClient(handler);
        var tmdb = new TmdbClient(http, NullLogger<TmdbClient>.Instance);
        var ingest = new FakeIngestionStatus { IsRunning = false };
        var sut = new TmdbEnricherService(_factory, settings, tmdb, tracker, ingest, new CardKeeper(), NullLogger<TmdbEnricherService>.Instance);

        using var hostCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await sut.StartAsync(hostCts.Token);
        try
        {
            // Wait for the initial 5-second startup delay + first-batch progress.
            await WaitForAsync(() => tracker.Enriched >= 5, TimeSpan.FromSeconds(45));
            Assert.Equal("running", tracker.State);

            // Pause.
            settings.Current = settings.Current with { TmdbEnrichmentEnabled = false };
            settings.RaiseChanged();

            // The Changed handler cancels the in-flight batch; tracker should
            // settle to idle within a few seconds.
            await WaitForAsync(() => tracker.State == "idle", TimeSpan.FromSeconds(10));
            var pausedAtCount = tracker.Enriched;

            // Genuinely paused: a few more seconds with no progress.
            await Task.Delay(TimeSpan.FromSeconds(2));
            Assert.Equal(pausedAtCount, tracker.Enriched);

            // Resume.
            settings.Current = settings.Current with { TmdbEnrichmentEnabled = true };
            settings.RaiseChanged();

            // The bug under test: after resume, processing must pick up again.
            // We require at least 5 fresh enrichments within 30 s of resume.
            await WaitForAsync(() => tracker.Enriched >= pausedAtCount + 5, TimeSpan.FromSeconds(30));
            Assert.Equal("running", tracker.State);
        }
        finally
        {
            await sut.StopAsync(hostCts.Token);
            http.Dispose();
        }
    }

    [Fact]
    public async Task Cancellation_during_batch_does_not_burn_attempts()
    {
        // Slow handler — every TMDB request takes 500 ms — so when we pause,
        // multiple in-flight requests get cancelled mid-flight. We assert that
        // none of those titles are recorded as 'failed' / have non-zero
        // Attempts.
        var settings = new FakeSettingsService();
        var tracker = new TmdbStatusTracker();
        var handler = new FakeTmdbHandler(_ => TimeSpan.FromMilliseconds(500));
        var http = new HttpClient(handler);
        var tmdb = new TmdbClient(http, NullLogger<TmdbClient>.Instance);
        var ingest = new FakeIngestionStatus { IsRunning = false };
        var sut = new TmdbEnricherService(_factory, settings, tmdb, tracker, ingest, new CardKeeper(), NullLogger<TmdbEnricherService>.Instance);

        using var hostCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await sut.StartAsync(hostCts.Token);
        try
        {
            await WaitForAsync(() => tracker.Enriched >= 1, TimeSpan.FromSeconds(45));

            settings.Current = settings.Current with { TmdbEnrichmentEnabled = false };
            settings.RaiseChanged();

            // Wait for the cancellation to settle.
            await WaitForAsync(() => tracker.State == "idle", TimeSpan.FromSeconds(15));
        }
        finally
        {
            await sut.StopAsync(hostCts.Token);
            http.Dispose();
        }

        // Assert: no titles ended up in 'failed' state. Cancellation should
        // not burn attempts on titles that simply got interrupted.
        await using var db = _factory.CreateDbContext();
        var falselyFailed = await db.CatalogTitleMetadata
            .Where(m => m.EnrichmentSource == "failed")
            .CountAsync();
        Assert.Equal(0, falselyFailed);

        // And: no metadata row should claim a failure with a cancellation
        // string in LastError.
        var cancelMarked = await db.CatalogTitleMetadata
            .Where(m => m.LastError != null
                     && (m.LastError!.Contains("canceled")
                         || m.LastError!.Contains("cancelled")))
            .CountAsync();
        Assert.Equal(0, cancelMarked);
    }

    private static async Task WaitForAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (predicate()) return;
            await Task.Delay(100);
        }
        Assert.Fail($"Condition not met within {timeout}.");
    }
}
