using LinkHarvester.Api.Health;
using LinkHarvester.Core;
using LinkHarvester.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LinkHarvester.Api.Tests;

public class HealthCheckServiceTests : IAsyncLifetime
{
    private string _dbPath = "";
    private string _connStr = "";
    private TestDbContextFactory _factory = null!;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"lh-health-test-{Guid.NewGuid():N}.db");
        _connStr = $"Data Source={_dbPath}";
        _factory = new TestDbContextFactory(_connStr);

        // Run migrations so the SELECT 1 has a real database to talk to.
        await using var db = _factory.CreateDbContext();
        await db.Database.MigrateAsync();
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
    public async Task Returns_ok_when_db_and_settings_both_healthy()
    {
        var settings = new FakeSettingsService();
        var sut = new HealthCheckService(_factory, settings);

        var report = await sut.CheckAsync(CancellationToken.None);

        Assert.True(report.Ok);
        Assert.True(report.Checks["db_ping"].Ok);
        Assert.True(report.Checks["settings_loaded"].Ok);
    }

    [Fact]
    public async Task Returns_fail_when_settings_snapshot_is_empty()
    {
        // An empty AuthUsername simulates the failure mode where the
        // DataProtection key ring couldn't decrypt the persisted settings
        // (or where the seed never wrote anything). The endpoint must
        // surface this via 503 — otherwise Fly happily routes traffic into
        // a process that can't authenticate anyone.
        var settings = new FakeSettingsService
        {
            Current = FakeSettingsService.Default with { AuthUsername = "" }
        };
        var sut = new HealthCheckService(_factory, settings);

        var report = await sut.CheckAsync(CancellationToken.None);

        Assert.False(report.Ok);
        Assert.True(report.Checks["db_ping"].Ok);
        Assert.False(report.Checks["settings_loaded"].Ok);
        Assert.NotNull(report.Checks["settings_loaded"].Detail);
    }

    [Fact]
    public async Task Returns_fail_when_db_path_is_unreachable()
    {
        // Point the factory at a directory that doesn't exist. The
        // SqliteConnection.Open call will throw; the health check must
        // catch it and report a structured failure rather than 500-ing
        // the whole endpoint.
        var bad = new TestDbContextFactory("Data Source=/no/such/dir/missing.db");
        var settings = new FakeSettingsService();
        var sut = new HealthCheckService(bad, settings);

        var report = await sut.CheckAsync(CancellationToken.None);

        Assert.False(report.Ok);
        Assert.False(report.Checks["db_ping"].Ok);
        Assert.True(report.Checks["settings_loaded"].Ok);
        Assert.NotNull(report.Checks["db_ping"].Detail);
    }
}
