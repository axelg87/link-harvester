using LinkHarvester.Core;
using LinkHarvester.Persistence;
using LinkHarvester.Persistence.Catalog;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinkHarvester.Api.Tests;

public class FollowingServiceTests : IAsyncLifetime
{
    private string _dbPath = "";
    private TestDbContextFactory _factory = null!;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"lh-following-{Guid.NewGuid()}.db");
        var conn = $"Data Source={_dbPath}";
        _factory = new TestDbContextFactory(conn);
        await using var db = _factory.CreateDbContext();
        await db.Database.EnsureCreatedAsync();
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) try { File.Delete(_dbPath); } catch { }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task GetAllAsync_returns_only_titles_with_episodes_and_prior_sends()
    {
        await SeedAsync(db =>
        {
            // Movie — has prior send but no episodes → excluded.
            var movie = new CatalogTitleEntity { CanonicalKey = "m1", TitleName = "Pulp Fiction", NormalizedTitle = "pulpfiction", CategoryName = "Films" };
            db.CatalogTitles.Add(movie);
            db.SaveChanges();
            db.Submissions.Add(new SubmissionEntity { Status = SubmissionStatus.Sent, CatalogTitleId = movie.Id, SubmittedAt = DateTimeOffset.UtcNow });

            // Series — has prior send AND episodes → included.
            var series = new CatalogTitleEntity { CanonicalKey = "s1", TitleName = "Severance", NormalizedTitle = "severance", CategoryName = "Séries" };
            db.CatalogTitles.Add(series);
            db.SaveChanges();
            db.CatalogEpisodes.Add(new CatalogEpisodeEntity { TitleId = series.Id, SeasonNumber = 1, EpisodeNumber = 1 });
            db.Submissions.Add(new SubmissionEntity { Status = SubmissionStatus.Sent, CatalogTitleId = series.Id, SubmittedAt = DateTimeOffset.UtcNow });

            // Series with no prior send → excluded.
            var other = new CatalogTitleEntity { CanonicalKey = "s2", TitleName = "Better Call Saul", NormalizedTitle = "bettercallsaul", CategoryName = "Séries" };
            db.CatalogTitles.Add(other);
            db.SaveChanges();
            db.CatalogEpisodes.Add(new CatalogEpisodeEntity { TitleId = other.Id, SeasonNumber = 1, EpisodeNumber = 1 });
            db.SaveChanges();
        });

        var svc = new FollowingService(_factory);
        var items = await svc.GetAllAsync(CancellationToken.None);

        Assert.Single(items);
        Assert.Equal("Severance", items[0].Title);
    }

    [Fact]
    public async Task Dismissed_titles_are_excluded()
    {
        int titleId = 0;
        await SeedAsync(db =>
        {
            var series = new CatalogTitleEntity { CanonicalKey = "s", TitleName = "X", NormalizedTitle = "x", CategoryName = "Séries" };
            db.CatalogTitles.Add(series);
            db.SaveChanges();
            db.CatalogEpisodes.Add(new CatalogEpisodeEntity { TitleId = series.Id, SeasonNumber = 1, EpisodeNumber = 1 });
            db.Submissions.Add(new SubmissionEntity { Status = SubmissionStatus.Sent, CatalogTitleId = series.Id, SubmittedAt = DateTimeOffset.UtcNow });
            db.SaveChanges();
            titleId = series.Id;
        });

        var svc = new FollowingService(_factory);
        Assert.Single(await svc.GetAllAsync(CancellationToken.None));

        await svc.DismissAsync(titleId, CancellationToken.None);
        Assert.Empty(await svc.GetAllAsync(CancellationToken.None));

        await svc.UndismissAsync(titleId, CancellationToken.None);
        Assert.Single(await svc.GetAllAsync(CancellationToken.None));
    }

    [Fact]
    public async Task No_time_decay_18mo_old_grab_still_appears()
    {
        await SeedAsync(db =>
        {
            var series = new CatalogTitleEntity { CanonicalKey = "old", TitleName = "Old Show", NormalizedTitle = "oldshow", CategoryName = "Séries" };
            db.CatalogTitles.Add(series);
            db.SaveChanges();
            db.CatalogEpisodes.Add(new CatalogEpisodeEntity { TitleId = series.Id, SeasonNumber = 1, EpisodeNumber = 1 });
            db.Submissions.Add(new SubmissionEntity
            {
                Status = SubmissionStatus.Sent,
                CatalogTitleId = series.Id,
                SubmittedAt = DateTimeOffset.UtcNow.AddDays(-540)
            });
            db.SaveChanges();
        });
        var svc = new FollowingService(_factory);
        var items = await svc.GetAllAsync(CancellationToken.None);
        Assert.Single(items);
    }

    private async Task SeedAsync(Action<HarvesterDbContext> seed)
    {
        await using var db = _factory.CreateDbContext();
        seed(db);
        await db.SaveChangesAsync();
    }
}
