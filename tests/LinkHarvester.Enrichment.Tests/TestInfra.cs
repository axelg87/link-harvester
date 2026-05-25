using System.Net;
using System.Text;
using LinkHarvester.Core;
using LinkHarvester.Persistence;
using LinkHarvester.Persistence.Catalog;
using Microsoft.EntityFrameworkCore;

namespace LinkHarvester.Enrichment.Tests;

/// <summary>
/// Manual <see cref="IDbContextFactory{TContext}"/> backed by a single
/// connection string. We deliberately avoid the pooled factory so each test
/// gets a clean DbContext per CreateDbContext call against a real on-disk
/// SQLite file.
/// </summary>
internal sealed class TestDbContextFactory : IDbContextFactory<HarvesterDbContext>
{
    private readonly string _connectionString;

    public TestDbContextFactory(string connectionString)
    {
        _connectionString = connectionString;
    }

    public HarvesterDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<HarvesterDbContext>()
            .UseSqlite(_connectionString)
            .Options;
        return new HarvesterDbContext(options);
    }
}

/// <summary>
/// In-memory <see cref="ISettingsService"/>. Tests mutate <see cref="Current"/>
/// and call <see cref="RaiseChanged"/> to simulate a settings update.
/// </summary>
internal sealed class FakeSettingsService : ISettingsService
{
    public AppSettingsSnapshot Current { get; set; } = Default;
    public event Action? Changed;

    public void RaiseChanged() => Changed?.Invoke();

    public Task LoadAsync(CancellationToken ct) => Task.CompletedTask;
    public Task UpdateAsync(AppSettingsSnapshot updated, CancellationToken ct)
    {
        Current = updated;
        RaiseChanged();
        return Task.CompletedTask;
    }

    public async Task<T> WithSnapshotAsync<T>(AppSettingsSnapshot snapshot, Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        var previous = Current;
        Current = snapshot;
        RaiseChanged();
        try { return await action(ct); }
        finally
        {
            Current = previous;
            RaiseChanged();
        }
    }

    public bool VerifyCredentials(string username, string password) => true;

    public static AppSettingsSnapshot Default => new(
        SynologyBaseUrl: "",
        SynologyUsername: "",
        SynologyPassword: "",
        SynologyOtpCode: null,
        SynologyMovieDestination: "video/movies",
        SynologySeriesDestination: "video/series",
        ScanIntervalMinutes: 30,
        ScanOnStartup: false,
        HosterPriority: Array.Empty<string>(),
        AuthUsername: "admin",
        AuthPassword: "x",
        TmdbApiKey: "test-key",
        TmdbEnrichmentEnabled: true,
        TmdbEnrichmentConcurrency: 2);
}

/// <summary>
/// Stub HttpMessageHandler that returns a canned, valid-looking TMDB
/// movie-detail JSON for every request, with a configurable artificial
/// delay so tests can drive the enricher into pause-while-busy states.
/// </summary>
internal sealed class FakeTmdbHandler : HttpMessageHandler
{
    private readonly Func<int, TimeSpan> _delayFor;
    private int _calls;

    public int CallCount => Volatile.Read(ref _calls);

    public FakeTmdbHandler(Func<int, TimeSpan>? delayFor = null)
    {
        _delayFor = delayFor ?? (_ => TimeSpan.FromMilliseconds(20));
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var n = Interlocked.Increment(ref _calls);
        var delay = _delayFor(n);
        if (delay > TimeSpan.Zero)
            await Task.Delay(delay, cancellationToken);

        // Return a minimal but valid TMDB movie payload. The enricher's parser
        // is tolerant of missing optional fields, so this is enough for the
        // success path.
        const string body = @"{
            ""id"": 12345,
            ""imdb_id"": ""tt0000001"",
            ""release_date"": ""2020-01-01"",
            ""runtime"": 100,
            ""vote_average"": 7.5,
            ""vote_count"": 100,
            ""popularity"": 50.0,
            ""original_language"": ""en"",
            ""overview"": ""x"",
            ""status"": ""Released"",
            ""genres"": [{""id"":1, ""name"": ""Drama""}]
        }";
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }
}

/// <summary>
/// Test double for <see cref="ICatalogIngestionStatus"/>. Tests flip
/// <see cref="IsRunning"/> to simulate the catalog ingestor holding the
/// SQLite writer.
/// </summary>
internal sealed class FakeIngestionStatus : ICatalogIngestionStatus
{
    public bool IsRunning { get; set; }
}

internal static class CatalogSeeder
{
    /// <summary>
    /// Seeds <paramref name="count"/> catalog titles. Each one has a
    /// <c>TmdbId</c> set so the enricher exercises its primary lookup path.
    /// </summary>
    public static async Task SeedTitlesAsync(IDbContextFactory<HarvesterDbContext> factory, int count)
    {
        await using var db = factory.CreateDbContext();
        await db.Database.MigrateAsync();
        for (var i = 1; i <= count; i++)
        {
            db.CatalogTitles.Add(new CatalogTitleEntity
            {
                CanonicalKey = $"tmdb:{i}",
                TitleName = $"Title {i}",
                NormalizedTitle = $"title {i}",
                TmdbId = i,
                CategoryName = "Films",
                FirstSeenAt = DateTimeOffset.UtcNow,
                LastSeenAt = DateTimeOffset.UtcNow
            });
        }
        await db.SaveChangesAsync();
    }
}
