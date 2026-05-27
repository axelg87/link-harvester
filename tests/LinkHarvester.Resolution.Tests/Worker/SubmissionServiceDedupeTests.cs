using LinkHarvester.Core;
using LinkHarvester.Persistence;
using LinkHarvester.Resolution;
using LinkHarvester.Worker;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinkHarvester.Resolution.Tests.Worker;

public class SubmissionServiceDedupeTests
{
    [Fact]
    public async Task SendToDsmAsync_returns_existing_submission_when_called_twice_in_window()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        var factory = new TestFactory(conn);

        TitleEntity title;
        ArticleEntity article;
        await using (var seed = factory.CreateDbContext())
        {
            await seed.Database.EnsureCreatedAsync();
            title = new TitleEntity
            {
                Canonical = "movie|x|2025|_",
                NormalizedTitle = "x",
                DisplayTitle = "X",
                Kind = TitleKind.Movie,
                Year = 2025,
                Status = TitleStatus.InInbox,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            seed.Titles.Add(title);
            await seed.SaveChangesAsync();
            article = new ArticleEntity
            {
                TitleId = title.Id,
                SourceId = "zt",
                ExternalId = "film:99999",
                Url = "https://www.zone-telechargement.news/?p=film&id=99999-x",
                DisplayTitle = "X",
                AggregatorDlProtectUrl = "https://dl-protect.link/abcdef0",
                HostersJson = "[]",
                ContentHash = "h",
                Status = ArticleStatus.Resolved,
                DiscoveredAt = DateTimeOffset.UtcNow,
            };
            seed.Articles.Add(article);
            await seed.SaveChangesAsync();

            // Existing Sent submission, recent enough to be in the dedupe window.
            seed.Submissions.Add(new SubmissionEntity
            {
                ArticleId = article.Id,
                Source = SendSourceKind.ZtArticle,
                DisplayTitle = "X",
                Destination = "video/movies",
                UrlCount = 2,
                SubmittedUrlsJson = "[\"u1\",\"u2\"]",
                DsmTaskIdsJson = "[\"t1\",\"t2\"]",
                Status = SubmissionStatus.Sent,
                SubmittedAt = DateTimeOffset.UtcNow.AddSeconds(-5),
                CompletedAt = DateTimeOffset.UtcNow.AddSeconds(-4),
            });
            await seed.SaveChangesAsync();
        }

        var dsm = new RecordingDsm();
        var resolver = new ThrowingResolver();
        var settings = new FakeSettings();
        var svc = new SubmissionService(factory, resolver, dsm, settings, NullLogger<SubmissionService>.Instance);

        var result = await svc.SendToDsmAsync(article.Id, CancellationToken.None);

        Assert.Equal(SubmissionStatus.Sent, result.Status);
        Assert.Equal(0, dsm.CreateTasksCalls);  // never called — dedupe short-circuits
        Assert.Equal(0, dsm.EnsureFoldersCalls);

        await using var verify = factory.CreateDbContext();
        var count = await verify.Submissions.CountAsync(s => s.ArticleId == article.Id);
        Assert.Equal(1, count);  // no second row created
    }

    [Fact]
    public async Task SendToDsmAsync_creates_new_submission_when_previous_is_older_than_window()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        var factory = new TestFactory(conn);

        ArticleEntity article;
        await using (var seed = factory.CreateDbContext())
        {
            await seed.Database.EnsureCreatedAsync();
            var title = new TitleEntity
            {
                Canonical = "movie|y|2025|_",
                NormalizedTitle = "y",
                DisplayTitle = "Y",
                Kind = TitleKind.Movie,
                Year = 2025,
                Status = TitleStatus.InInbox,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            seed.Titles.Add(title);
            await seed.SaveChangesAsync();
            article = new ArticleEntity
            {
                TitleId = title.Id,
                SourceId = "zt",
                ExternalId = "film:88888",
                Url = "https://www.zone-telechargement.news/?p=film&id=88888-y",
                DisplayTitle = "Y",
                AggregatorDlProtectUrl = "https://dl-protect.link/abcdef1",
                HostersJson = "[]",
                ContentHash = "h",
                Status = ArticleStatus.Resolved,
                DiscoveredAt = DateTimeOffset.UtcNow,
            };
            seed.Articles.Add(article);
            await seed.SaveChangesAsync();

            // Submission OLDER than the dedupe window — must not block a new send.
            seed.Submissions.Add(new SubmissionEntity
            {
                ArticleId = article.Id,
                Source = SendSourceKind.ZtArticle,
                DisplayTitle = "Y",
                Status = SubmissionStatus.Sent,
                SubmittedAt = DateTimeOffset.UtcNow - SubmissionService.DuplicateSendWindow - TimeSpan.FromMinutes(1),
            });

            // Need at least one resolved link so resolution doesn't short-circuit.
            seed.ResolvedLinks.Add(new ResolvedLinkEntity
            {
                ArticleId = article.Id,
                Hoster = "Uploady",
                Url = "https://uploady.io/file/abc",
                ResolvedAt = DateTimeOffset.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        var dsm = new RecordingDsm();
        var resolver = new ThrowingResolver();
        var settings = new FakeSettings();
        var svc = new SubmissionService(factory, resolver, dsm, settings, NullLogger<SubmissionService>.Instance);

        var result = await svc.SendToDsmAsync(article.Id, CancellationToken.None);

        Assert.NotEqual(SubmissionStatus.ResolutionFailed, result.Status);
        Assert.Equal(1, dsm.CreateTasksCalls);
        await using var verify = factory.CreateDbContext();
        var count = await verify.Submissions.CountAsync(s => s.ArticleId == article.Id);
        Assert.Equal(2, count);  // old + new
    }

    // --- helpers ---
    private sealed class TestFactory : IDbContextFactory<HarvesterDbContext>
    {
        private readonly SqliteConnection _conn;
        public TestFactory(SqliteConnection conn) { _conn = conn; }
        public HarvesterDbContext CreateDbContext()
        {
            var opts = new DbContextOptionsBuilder<HarvesterDbContext>().UseSqlite(_conn).Options;
            return new HarvesterDbContext(opts);
        }
    }

    private sealed class RecordingDsm : IDownloadStationClient
    {
        public int CreateTasksCalls { get; private set; }
        public int EnsureFoldersCalls { get; private set; }
        public Task<IReadOnlyList<string>> CreateTasksAsync(IEnumerable<string> urls, string? destination, CancellationToken ct)
        {
            CreateTasksCalls++;
            return Task.FromResult<IReadOnlyList<string>>(new[] { "task-1" });
        }
        public Task EnsureFoldersAsync(IEnumerable<string> folderPaths, CancellationToken ct)
        {
            EnsureFoldersCalls++;
            return Task.CompletedTask;
        }
        public Task<DsmListResult> ListFolderAsync(string folderPath, CancellationToken ct)
            => Task.FromResult(new DsmListResult(Exists: false, FolderPath: folderPath, Files: Array.Empty<DsmFileEntry>()));
    }

    private sealed class ThrowingResolver : ILinkResolver
    {
        public Task<ResolutionOutcome> ResolveAsync(string protectedUrl, CancellationToken ct)
            => throw new InvalidOperationException("Resolver should not be called by these tests");
    }

    private sealed class FakeSettings : ISettingsService
    {
        public AppSettingsSnapshot Current { get; } = new(
            SynologyBaseUrl: "", SynologyUsername: "u", SynologyPassword: "p",
            SynologyOtpCode: null,
            SynologyMovieDestination: "video/movies",
            SynologySeriesDestination: "video/series",
            SynologyAnimeDestination: "video/anime",
            ScanIntervalMinutes: 30, ScanOnStartup: false,
            HosterPriority: Array.Empty<string>(),
            AuthUsername: "a", AuthPassword: "b",
            TmdbApiKey: "", TmdbEnrichmentEnabled: false, TmdbEnrichmentConcurrency: 4);
        public event Action? Changed { add { } remove { } }
        public Task LoadAsync(CancellationToken ct) => Task.CompletedTask;
        public Task UpdateAsync(AppSettingsSnapshot updated, CancellationToken ct) => Task.CompletedTask;
        public bool VerifyCredentials(string username, string password) => true;
    }
}
