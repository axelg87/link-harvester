using LinkHarvester.Core;
using LinkHarvester.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LinkHarvester.Api.Tests;

internal sealed class TestDbContextFactory : IDbContextFactory<HarvesterDbContext>
{
    private readonly string _connectionString;
    public TestDbContextFactory(string connectionString) => _connectionString = connectionString;

    public HarvesterDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<HarvesterDbContext>()
            .UseSqlite(_connectionString)
            .Options;
        return new HarvesterDbContext(options);
    }
}

internal sealed class FakeSettingsService : ISettingsService
{
    public AppSettingsSnapshot Current { get; set; } = Default;
    public event Action? Changed;

    public Task LoadAsync(CancellationToken ct) => Task.CompletedTask;
    public Task UpdateAsync(AppSettingsSnapshot updated, CancellationToken ct)
    {
        Current = updated;
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    public async Task<T> WithSnapshotAsync<T>(AppSettingsSnapshot snapshot, Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        var previous = Current;
        Current = snapshot;
        Changed?.Invoke();
        try { return await action(ct); }
        finally
        {
            Current = previous;
            Changed?.Invoke();
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
        TmdbApiKey: "",
        TmdbEnrichmentEnabled: false,
        TmdbEnrichmentConcurrency: 4);
}
