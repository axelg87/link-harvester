using LinkHarvester.Api.Maintenance;
using LinkHarvester.Persistence;
using LinkHarvester.Persistence.Catalog;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LinkHarvester.Api.Tests;

public class EnrichmentMaintenanceTests : IAsyncLifetime
{
    private string _dbPath = "";
    private string _connStr = "";
    private TestDbContextFactory _factory = null!;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"lh-maint-test-{Guid.NewGuid():N}.db");
        _connStr = $"Data Source={_dbPath}";
        _factory = new TestDbContextFactory(_connStr);
        await using var db = _factory.CreateDbContext();
        await db.Database.MigrateAsync();
        await SeedAsync(db);
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

    /// <summary>
    /// Seeds 10 catalog titles whose metadata rows cover every state we care
    /// about: 5 transient failures (matching the lock/timeout patterns), 2
    /// genuine failures (e.g. no_tmdb_match), 2 successes, and 1 pending.
    /// </summary>
    private static async Task SeedAsync(HarvesterDbContext db)
    {
        for (var i = 1; i <= 10; i++)
        {
            db.CatalogTitles.Add(new CatalogTitleEntity
            {
                Id = i,
                CanonicalKey = $"tmdb:{i}",
                TitleName = $"Title {i}",
                NormalizedTitle = $"title {i}",
                CategoryName = "Films",
                FirstSeenAt = DateTimeOffset.UtcNow,
                LastSeenAt = DateTimeOffset.UtcNow,
            });
        }
        await db.SaveChangesAsync();

        var rows = new[]
        {
            // 5 transient failures, exercising every pattern variant.
            new CatalogTitleMetadataEntity { TitleId = 1, EnrichmentSource = "failed", Attempts = 5, LastError = "SQLite Error 5: 'database is locked'." },
            new CatalogTitleMetadataEntity { TitleId = 2, EnrichmentSource = "failed", Attempts = 5, LastError = "database is busy at line 42" },
            new CatalogTitleMetadataEntity { TitleId = 3, EnrichmentSource = "failed", Attempts = 5, LastError = "The operation has timed out." },
            new CatalogTitleMetadataEntity { TitleId = 4, EnrichmentSource = "failed", Attempts = 5, LastError = "command timeout exceeded" },
            new CatalogTitleMetadataEntity { TitleId = 5, EnrichmentSource = "failed", Attempts = 5, LastError = "SQLite Error 6: locked" },
            // 2 genuine failures.
            new CatalogTitleMetadataEntity { TitleId = 6, EnrichmentSource = "failed", Attempts = 5, LastError = "no_tmdb_match" },
            new CatalogTitleMetadataEntity { TitleId = 7, EnrichmentSource = "failed", Attempts = 5, LastError = "TMDB 401: invalid api key" },
            // 2 successes — must never be touched.
            new CatalogTitleMetadataEntity { TitleId = 8, EnrichmentSource = "tmdb_direct", Attempts = 1, LastError = null, Year = 2020 },
            new CatalogTitleMetadataEntity { TitleId = 9, EnrichmentSource = "tmdb_imdb_lookup", Attempts = 2, LastError = null, Year = 2019 },
            // 1 pending in flight.
            new CatalogTitleMetadataEntity { TitleId = 10, EnrichmentSource = "pending", Attempts = 0, LastError = null },
        };
        foreach (var r in rows) db.CatalogTitleMetadata.Add(r);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task CountTransientFailedAsync_returns_5_for_seed()
    {
        await using var db = _factory.CreateDbContext();
        var n = await EnrichmentMaintenance.CountTransientFailedAsync(db, CancellationToken.None);
        Assert.Equal(5, n);
    }

    [Fact]
    public async Task ResetTransientFailedAsync_resets_only_transient_rows()
    {
        await using (var db = _factory.CreateDbContext())
        {
            var n = await EnrichmentMaintenance.ResetTransientFailedAsync(db, CancellationToken.None);
            Assert.Equal(5, n);
        }

        await using (var db = _factory.CreateDbContext())
        {
            // The 5 transient ones now pending, with cleared LastError and Attempts.
            var pendingFromTransient = await db.CatalogTitleMetadata
                .Where(m => m.TitleId >= 1 && m.TitleId <= 5)
                .ToListAsync();
            Assert.Equal(5, pendingFromTransient.Count);
            Assert.All(pendingFromTransient, m =>
            {
                Assert.Equal("pending", m.EnrichmentSource);
                Assert.Equal(0, m.Attempts);
                Assert.Null(m.LastError);
            });

            // The 2 genuine failures still failed, attempts intact.
            var stillFailed = await db.CatalogTitleMetadata
                .Where(m => m.EnrichmentSource == "failed")
                .ToListAsync();
            Assert.Equal(2, stillFailed.Count);
            Assert.Contains(stillFailed, m => m.LastError == "no_tmdb_match");
            Assert.Contains(stillFailed, m => m.LastError!.Contains("invalid api key"));

            // Successes untouched.
            var success = await db.CatalogTitleMetadata
                .Where(m => m.EnrichmentSource == "tmdb_direct" || m.EnrichmentSource == "tmdb_imdb_lookup")
                .CountAsync();
            Assert.Equal(2, success);
        }
    }

    [Fact]
    public async Task ResetAllFailedAsync_resets_every_failed_row_including_genuine()
    {
        await using (var db = _factory.CreateDbContext())
        {
            var n = await EnrichmentMaintenance.ResetAllFailedAsync(db, CancellationToken.None);
            Assert.Equal(7, n);
        }

        await using (var db = _factory.CreateDbContext())
        {
            // No rows should remain in the failed bucket.
            var stillFailed = await db.CatalogTitleMetadata.CountAsync(m => m.EnrichmentSource == "failed");
            Assert.Equal(0, stillFailed);

            // All 7 ex-failures are now pending with cleared state.
            var pending = await db.CatalogTitleMetadata
                .Where(m => m.EnrichmentSource == "pending"
                         && m.TitleId >= 1 && m.TitleId <= 7)
                .ToListAsync();
            Assert.Equal(7, pending.Count);
            Assert.All(pending, m =>
            {
                Assert.Equal(0, m.Attempts);
                Assert.Null(m.LastError);
            });

            // Successes still untouched.
            var success = await db.CatalogTitleMetadata
                .Where(m => m.EnrichmentSource == "tmdb_direct" || m.EnrichmentSource == "tmdb_imdb_lookup")
                .CountAsync();
            Assert.Equal(2, success);
        }
    }

    [Fact]
    public async Task Idempotent_second_reset_is_a_no_op()
    {
        await using (var db = _factory.CreateDbContext())
        {
            await EnrichmentMaintenance.ResetTransientFailedAsync(db, CancellationToken.None);
        }
        await using (var db = _factory.CreateDbContext())
        {
            var n2 = await EnrichmentMaintenance.ResetTransientFailedAsync(db, CancellationToken.None);
            Assert.Equal(0, n2);
        }
    }
}
