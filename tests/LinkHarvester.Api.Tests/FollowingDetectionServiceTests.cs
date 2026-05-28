using LinkHarvester.Core;
using LinkHarvester.Persistence;
using LinkHarvester.Persistence.Catalog;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinkHarvester.Api.Tests;

public class FollowingDetectionServiceTests : IAsyncLifetime
{
    private string _dbPath = "";
    private TestDbContextFactory _factory = null!;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"lh-detection-{Guid.NewGuid()}.db");
        _factory = new TestDbContextFactory($"Data Source={_dbPath}");
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
    public async Task Writes_log_row_when_new_episode_for_followed_series()
    {
        var (titleId, articleId) = await SeedFollowedSeriesWithNewEpisodeAsync();

        var svc = new FollowingDetectionService(_factory, NullLogger<FollowingDetectionService>.Instance);
        await svc.RegisterIngestedAsync(articleId, CancellationToken.None);

        await using var db = _factory.CreateDbContext();
        var detections = await db.FollowingDetectionLog.ToListAsync();
        Assert.Single(detections);
        Assert.Equal(titleId, detections[0].CatalogTitleId);
        Assert.Equal("S01E02", detections[0].EpisodeCode);
        Assert.Null(detections[0].ConsumedAt);
    }

    [Fact]
    public async Task Reingest_of_same_article_does_not_duplicate_detection()
    {
        var (_, articleId) = await SeedFollowedSeriesWithNewEpisodeAsync();
        var svc = new FollowingDetectionService(_factory, NullLogger<FollowingDetectionService>.Instance);

        await svc.RegisterIngestedAsync(articleId, CancellationToken.None);
        await svc.RegisterIngestedAsync(articleId, CancellationToken.None);

        await using var db = _factory.CreateDbContext();
        var count = await db.FollowingDetectionLog.CountAsync();
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task No_detection_for_titles_with_no_prior_submission()
    {
        // Set up a series with episodes but no prior Sent submission.
        int articleId = 0;
        await SeedAsync(db =>
        {
            var series = new CatalogTitleEntity { CanonicalKey = "s", TitleName = "X", NormalizedTitle = "x", CategoryName = "Séries" };
            db.CatalogTitles.Add(series);
            db.SaveChanges();
            var ep = new CatalogEpisodeEntity { TitleId = series.Id, SeasonNumber = 1, EpisodeNumber = 1 };
            db.CatalogEpisodes.Add(ep);
            db.SaveChanges();
            articleId = 42;
            db.CatalogLinks.Add(new CatalogLinkEntity
            {
                ExternalLinkId = 1, TitleId = series.Id, EpisodeId = ep.Id,
                HarvesterArticleId = articleId, LinkSource = "zt", LinkUrl = "https://x", HostName = "1fichier", NormalizedHost = "1fichier"
            });
        });

        var svc = new FollowingDetectionService(_factory, NullLogger<FollowingDetectionService>.Instance);
        await svc.RegisterIngestedAsync(articleId, CancellationToken.None);

        await using var db = _factory.CreateDbContext();
        Assert.Empty(await db.FollowingDetectionLog.ToListAsync());
    }

    private async Task<(int titleId, int articleId)> SeedFollowedSeriesWithNewEpisodeAsync()
    {
        int seriesId = 0, newArticleId = 0;
        await SeedAsync(db =>
        {
            // Title + Articles satisfy FK constraints from SubmissionEntity.ArticleId.
            var title = new TitleEntity { Canonical = "zt:severance", DisplayTitle = "Severance", NormalizedTitle = "severance" };
            db.Titles.Add(title);
            db.SaveChanges();
            var article1 = new ArticleEntity { TitleId = title.Id, SourceId = "zt", ExternalId = "e1", Url = "https://a1", DiscoveredAt = DateTimeOffset.UtcNow };
            var article2 = new ArticleEntity { TitleId = title.Id, SourceId = "zt", ExternalId = "e2", Url = "https://a2", DiscoveredAt = DateTimeOffset.UtcNow };
            db.Articles.Add(article1);
            db.Articles.Add(article2);
            db.SaveChanges();

            var series = new CatalogTitleEntity { CanonicalKey = "s", TitleName = "Severance", NormalizedTitle = "severance", CategoryName = "Séries" };
            db.CatalogTitles.Add(series);
            db.SaveChanges();
            seriesId = series.Id;

            var ep1 = new CatalogEpisodeEntity { TitleId = series.Id, SeasonNumber = 1, EpisodeNumber = 1 };
            db.CatalogEpisodes.Add(ep1);
            db.SaveChanges();
            db.CatalogLinks.Add(new CatalogLinkEntity
            {
                ExternalLinkId = 100, TitleId = series.Id, EpisodeId = ep1.Id,
                HarvesterArticleId = article1.Id, LinkSource = "zt", LinkUrl = "https://e1", HostName = "1fichier", NormalizedHost = "1fichier"
            });
            db.Submissions.Add(new SubmissionEntity
            {
                Status = SubmissionStatus.Sent, CatalogTitleId = series.Id,
                ArticleId = article1.Id, SubmittedAt = DateTimeOffset.UtcNow.AddDays(-7)
            });

            newArticleId = article2.Id;
            var ep2 = new CatalogEpisodeEntity { TitleId = series.Id, SeasonNumber = 1, EpisodeNumber = 2 };
            db.CatalogEpisodes.Add(ep2);
            db.SaveChanges();
            db.CatalogLinks.Add(new CatalogLinkEntity
            {
                ExternalLinkId = 200, TitleId = series.Id, EpisodeId = ep2.Id,
                HarvesterArticleId = newArticleId, LinkSource = "zt", LinkUrl = "https://e2", HostName = "1fichier", NormalizedHost = "1fichier"
            });
        });
        return (seriesId, newArticleId);
    }

    private async Task SeedAsync(Action<HarvesterDbContext> seed)
    {
        await using var db = _factory.CreateDbContext();
        seed(db);
        await db.SaveChangesAsync();
    }
}
