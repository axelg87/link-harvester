using System.Net;
using LinkHarvester.Persistence;
using LinkHarvester.Persistence.Cards;
using LinkHarvester.Persistence.Catalog;
using LinkHarvester.Resolution.HealthCheck;
using LinkHarvester.Resolution.HealthCheck.Hosters;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using LinkHarvester.Worker.Backfill;
using Xunit;

namespace LinkHarvester.Resolution.Tests.Worker;

public class LinkHealthSweepServiceTests
{
    [Fact]
    public async Task Sweep_marks_links_alive_or_dead_and_hides_all_dead_titles()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        var factory = new TestFactory(conn);

        // Seed: 2 titles, each with 2 links. Title A: 1 alive, 1 dead → stays visible.
        // Title B: 2 dead → hidden after sweep.
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
            db.CatalogTitles.AddRange(
                new CatalogTitleEntity
                {
                    CanonicalKey = "k1",
                    TitleName = "A",
                    NormalizedTitle = "a",
                    CategoryName = "Films",
                    FirstSeenAt = DateTimeOffset.UtcNow,
                    LastSeenAt = DateTimeOffset.UtcNow,
                    Links =
                    {
                        new CatalogLinkEntity { ExternalLinkId = 1, LinkUrl = "https://1fichier.com/?aliveA", HostName = "1Fichier", NormalizedHost = "1fichier", CreatedAt = DateTimeOffset.UtcNow },
                        new CatalogLinkEntity { ExternalLinkId = 2, LinkUrl = "https://uploady.io/deadA", HostName = "Uploady", NormalizedHost = "uploady", CreatedAt = DateTimeOffset.UtcNow },
                    },
                },
                new CatalogTitleEntity
                {
                    CanonicalKey = "k2",
                    TitleName = "B",
                    NormalizedTitle = "b",
                    CategoryName = "Films",
                    FirstSeenAt = DateTimeOffset.UtcNow,
                    LastSeenAt = DateTimeOffset.UtcNow,
                    Links =
                    {
                        new CatalogLinkEntity { ExternalLinkId = 3, LinkUrl = "https://uploady.io/dead1", HostName = "Uploady", NormalizedHost = "uploady", CreatedAt = DateTimeOffset.UtcNow },
                        new CatalogLinkEntity { ExternalLinkId = 4, LinkUrl = "https://uploady.io/dead2", HostName = "Uploady", NormalizedHost = "uploady", CreatedAt = DateTimeOffset.UtcNow },
                    },
                });
            await db.SaveChangesAsync();
        }

        var health = new FakeHealthService(url => url.Contains("alive", StringComparison.OrdinalIgnoreCase)
            ? LinkHealth.Alive
            : LinkHealth.Dead);
        var sut = new LinkHealthSweepService(factory, health, new CardKeeper(), NullLogger<LinkHealthSweepService>.Instance);

        var run = await sut.RunAsync(hosterFilter: null, resume: false, CancellationToken.None);

        Assert.Equal("succeeded", run.Status);
        Assert.Equal(4, run.Checked);
        Assert.Equal(1, run.Alive);
        Assert.Equal(3, run.Dead);
        Assert.Equal(1, run.HiddenTitles);

        await using var db2 = factory.CreateDbContext();
        var titleA = await db2.CatalogTitles.FirstAsync(t => t.CanonicalKey == "k1");
        var titleB = await db2.CatalogTitles.FirstAsync(t => t.CanonicalKey == "k2");
        Assert.False(titleA.IsHidden);
        Assert.True(titleB.IsHidden);
        Assert.Equal("all-links-dead", titleB.HiddenReason);
    }

    [Fact]
    public async Task Sweep_resumes_from_cursor_when_resume_true()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        var factory = new TestFactory(conn);

        await using (var db = factory.CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
            var t = new CatalogTitleEntity
            {
                CanonicalKey = "k",
                TitleName = "T",
                NormalizedTitle = "t",
                CategoryName = "Films",
                FirstSeenAt = DateTimeOffset.UtcNow,
                LastSeenAt = DateTimeOffset.UtcNow,
            };
            for (int i = 1; i <= 5; i++)
            {
                t.Links.Add(new CatalogLinkEntity
                {
                    ExternalLinkId = i,
                    LinkUrl = $"https://uploady.io/x{i}",
                    HostName = "Uploady",
                    NormalizedHost = "uploady",
                    CreatedAt = DateTimeOffset.UtcNow,
                });
            }
            db.CatalogTitles.Add(t);
            await db.SaveChangesAsync();

            // Seed a prior unfinished sweep at link id = 3 — resumed sweep should
            // skip the first 3 links and only check the remaining 2.
            db.HealthSweepRuns.Add(new HealthSweepRunEntity
            {
                StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                LastCheckedCatalogLinkId = 3,
                Checked = 3,
                Alive = 0,
                Dead = 0,
                Unknown = 3,
                Status = "running",
            });
            await db.SaveChangesAsync();
        }

        var health = new FakeHealthService(_ => LinkHealth.Alive);
        var sut = new LinkHealthSweepService(factory, health, new CardKeeper(), NullLogger<LinkHealthSweepService>.Instance);

        var run = await sut.RunAsync(hosterFilter: null, resume: true, CancellationToken.None);

        Assert.Equal(2, run.Checked - 3); // 2 newly-checked on top of the 3 from before
        Assert.Equal("succeeded", run.Status);
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

    private sealed class FakeHealthService : ILinkHealthService
    {
        private readonly Func<string, LinkHealth> _verdict;
        public FakeHealthService(Func<string, LinkHealth> verdict) { _verdict = verdict; }
        public Task<HealthCheckResult> CheckAsync(string url, string? hosterName, CancellationToken ct)
            => Task.FromResult(new HealthCheckResult(_verdict(url), 200, null, "fake", TimeSpan.Zero));
        public async Task<HealthCheckResult> CheckUntilAliveAsync(IEnumerable<(string url, string? hoster)> candidates, CancellationToken ct)
        {
            HealthCheckResult? last = null;
            foreach (var (u, h) in candidates)
            {
                var r = await CheckAsync(u, h, ct);
                last = r;
                if (r.Health != LinkHealth.Dead) return r;
            }
            return last ?? new HealthCheckResult(LinkHealth.Unknown, null, null, "fake", TimeSpan.Zero);
        }
    }
}
