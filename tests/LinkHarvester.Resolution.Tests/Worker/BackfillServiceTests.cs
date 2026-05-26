using LinkHarvester.Core;
using LinkHarvester.Persistence;
using LinkHarvester.Persistence.Catalog;
using LinkHarvester.Worker.Backfill;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinkHarvester.Resolution.Tests.Worker;

public class BackfillServiceTests
{
    [Fact]
    public async Task GetOrCreateRunAsync_creates_fresh_row_when_none_exists()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        var factory = new TestFactory(conn);
        await using (var seed = factory.CreateDbContext()) await seed.Database.EnsureCreatedAsync();

        // ScanPipeline is non-null but we won't exercise it — only the cursor APIs.
        var sut = new BackfillService(factory, Array.Empty<IBackfillFeedSource>(), scanPipeline: null!, NullLogger<BackfillService>.Instance);

        var from = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var run = await sut.GetOrCreateRunAsync("zt", "films", from, startPage: 1, CancellationToken.None);

        Assert.NotEqual(0, run.Id);
        Assert.Equal("running", run.Status);
        Assert.Equal("zt", run.SourceId);
        Assert.Equal("films", run.Kind);
    }

    [Fact]
    public async Task GetOrCreateRunAsync_resumes_unfinished_run_for_same_key()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        var factory = new TestFactory(conn);
        var from = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

        await using (var seed = factory.CreateDbContext())
        {
            await seed.Database.EnsureCreatedAsync();
            seed.BackfillRuns.Add(new BackfillRunEntity
            {
                SourceId = "zt", Kind = "films", FromDate = from,
                StartPage = 1, LastCompletedPage = 42,
                Discovered = 100, Promoted = 80, Skipped = 20,
                StartedAt = DateTimeOffset.UtcNow.AddHours(-1),
                Status = "running",
            });
            await seed.SaveChangesAsync();
        }

        var sut = new BackfillService(factory, Array.Empty<IBackfillFeedSource>(), scanPipeline: null!, NullLogger<BackfillService>.Instance);
        var run = await sut.GetOrCreateRunAsync("zt", "films", from, startPage: 1, CancellationToken.None);

        Assert.Equal(42, run.LastCompletedPage);
        Assert.Equal(80, run.Promoted);
    }

    [Fact]
    public async Task GetOrCreateRunAsync_creates_new_row_when_previous_succeeded()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        var factory = new TestFactory(conn);
        var from = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

        int firstId;
        await using (var seed = factory.CreateDbContext())
        {
            await seed.Database.EnsureCreatedAsync();
            var first = new BackfillRunEntity
            {
                SourceId = "zt", Kind = "films", FromDate = from,
                StartPage = 1, LastCompletedPage = 99,
                StartedAt = DateTimeOffset.UtcNow.AddDays(-1),
                FinishedAt = DateTimeOffset.UtcNow.AddDays(-1).AddHours(2),
                Status = "succeeded",
            };
            seed.BackfillRuns.Add(first);
            await seed.SaveChangesAsync();
            firstId = first.Id;
        }

        var sut = new BackfillService(factory, Array.Empty<IBackfillFeedSource>(), scanPipeline: null!, NullLogger<BackfillService>.Instance);
        var run = await sut.GetOrCreateRunAsync("zt", "films", from, startPage: 1, CancellationToken.None);

        Assert.NotEqual(firstId, run.Id);
        Assert.Equal(0, run.LastCompletedPage);
        Assert.Equal("running", run.Status);
    }

    private sealed class TestFactory : IDbContextFactory<HarvesterDbContext>
    {
        private readonly SqliteConnection _conn;
        public TestFactory(SqliteConnection conn) { _conn = conn; }
        public HarvesterDbContext CreateDbContext()
        {
            var opts = new DbContextOptionsBuilder<HarvesterDbContext>()
                .UseSqlite(_conn)
                .Options;
            return new HarvesterDbContext(opts);
        }
    }
}
