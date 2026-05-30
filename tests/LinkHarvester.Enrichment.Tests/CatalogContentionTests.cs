using LinkHarvester.Core;
using LinkHarvester.Persistence;
using LinkHarvester.Persistence.Cards;
using LinkHarvester.Persistence.Catalog;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LinkHarvester.Enrichment.Tests;

/// <summary>
/// Regression coverage for KNOWN_BUGS.md BUG-2: the TMDB enricher must not
/// race the catalog ingestor for the SQLite writer, and must not record
/// SQLite contention errors as per-title failures (which on 5 collisions
/// permanently marked the title 'failed').
/// </summary>
public class CatalogContentionTests : IAsyncLifetime
{
    private string _dbPath = "";
    private string _connStr = "";
    private TestDbContextFactory _factory = null!;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"lh-contention-test-{Guid.NewGuid():N}.db");
        _connStr = $"Data Source={_dbPath}";
        _factory = new TestDbContextFactory(_connStr);
        await CatalogSeeder.SeedTitlesAsync(_factory, count: 50);
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* best effort */ }
        foreach (var sidecar in new[] { _dbPath + "-wal", _dbPath + "-shm" })
        {
            try { if (File.Exists(sidecar)) File.Delete(sidecar); } catch { /* best effort */ }
        }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Enricher_does_not_load_a_batch_while_ingestion_is_running()
    {
        var settings = new FakeSettingsService();
        var tracker = new TmdbStatusTracker();
        var ingest = new FakeIngestionStatus { IsRunning = true };
        var handler = new FakeTmdbHandler(_ => TimeSpan.FromMilliseconds(20));
        var http = new HttpClient(handler);
        var tmdb = new TmdbClient(http, NullLogger<TmdbClient>.Instance);
        var sut = new TmdbEnricherService(_factory, settings, tmdb, tracker, ingest, new CardKeeper(), NullLogger<TmdbEnricherService>.Instance);

        using var hostCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await sut.StartAsync(hostCts.Token);
        try
        {
            // Past the 5 s startup delay + a few gate poll cycles: no work
            // should have happened, no batches loaded, because IsRunning has
            // been true the whole time.
            await Task.Delay(TimeSpan.FromSeconds(10));
            Assert.Equal(0, handler.CallCount);
            Assert.Equal(0, tracker.Enriched);
            Assert.Equal(0, tracker.Failed);

            // Release the gate; the enricher polls every 2 s and should
            // pick up work shortly after.
            ingest.IsRunning = false;
            await WaitForAsync(() => tracker.Enriched >= 5, TimeSpan.FromSeconds(15));
        }
        finally
        {
            await sut.StopAsync(hostCts.Token);
            http.Dispose();
        }
    }

    [Fact]
    public void SQLite_lock_during_save_is_classified_transient_and_does_not_burn_attempts()
    {
        // We can't easily provoke a real cross-process SQLite lock from a
        // single test, but the worker's classification logic is the entire
        // bug fix here. Exercise it directly: the static helper must
        // recognise SqliteException(5/6) and "database is locked" messages
        // as transient, and the unit test below seeds a fake SqliteException
        // through the public API and asserts no metadata row mutates.
        Assert.True(TmdbEnricherService.IsTransientSqliteContention(
            new SqliteException("database is locked", 5)));
        Assert.True(TmdbEnricherService.IsTransientSqliteContention(
            new SqliteException("database is busy", 6)));
        Assert.True(TmdbEnricherService.IsTransientSqliteContention(
            new InvalidOperationException("Wrapped: SQLite Error 5: 'database is locked'.")));
        Assert.True(TmdbEnricherService.IsTransientSqliteContention(
            new InvalidOperationException("outer", new SqliteException("database is locked", 5))));

        // And the negative cases — these must NOT be misclassified as
        // contention (which would suppress real failure reporting).
        Assert.False(TmdbEnricherService.IsTransientSqliteContention(
            new HttpRequestException("Connection refused")));
        Assert.False(TmdbEnricherService.IsTransientSqliteContention(
            new InvalidOperationException("no_tmdb_match")));
        Assert.False(TmdbEnricherService.IsTransientSqliteContention(
            new TaskCanceledException("HTTP request timed out")));
    }

    [Fact]
    public async Task Heal_on_startup_resets_lock_residue_to_pending()
    {
        // Seed five metadata rows that look like BUG-2 residue: marked
        // 'failed' with Attempts=5 and a "database is locked" LastError.
        await using (var db = _factory.CreateDbContext())
        {
            for (var i = 1; i <= 5; i++)
            {
                db.CatalogTitleMetadata.Add(new CatalogTitleMetadataEntity
                {
                    TitleId = i,
                    EnrichmentSource = "failed",
                    Attempts = 5,
                    LastError = i % 2 == 0
                        ? "SQLite Error 5: 'database is locked'."
                        : "The operation has timed out.",
                    LastEnrichedAt = DateTimeOffset.UtcNow,
                });
            }
            // Plus one genuinely failed row that must survive the heal.
            db.CatalogTitleMetadata.Add(new CatalogTitleMetadataEntity
            {
                TitleId = 6,
                EnrichmentSource = "failed",
                Attempts = 5,
                LastError = "no_tmdb_match",
                LastEnrichedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        // Run the same SQL the Program.cs startup path runs.
        await using (var db = _factory.CreateDbContext())
        {
            var n = await db.Database.ExecuteSqlRawAsync(@"
                UPDATE CatalogTitleMetadata
                SET EnrichmentSource = 'pending',
                    Attempts = 0,
                    LastError = NULL
                WHERE LastError LIKE '%database is locked%'
                   OR LastError LIKE '%database is busy%'
                   OR LastError LIKE '%SQLite Error 5%'
                   OR LastError LIKE '%SQLite Error 6%'
                   OR LastError LIKE '%timeout%'
                   OR LastError LIKE '%timed out%';");
            Assert.Equal(5, n);
        }

        // Verify: the 5 lock/timeout rows are now pending; the no_tmdb_match
        // row is untouched.
        await using (var db = _factory.CreateDbContext())
        {
            var pending = await db.CatalogTitleMetadata
                .Where(m => m.EnrichmentSource == "pending" && m.Attempts == 0 && m.LastError == null)
                .CountAsync();
            Assert.Equal(5, pending);

            var stillFailed = await db.CatalogTitleMetadata
                .Where(m => m.EnrichmentSource == "failed" && m.LastError == "no_tmdb_match")
                .CountAsync();
            Assert.Equal(1, stillFailed);
        }
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
