using LinkHarvester.Core;
using LinkHarvester.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LinkHarvester.Api.Health;

/// <summary>
/// Internal liveness/readiness checks for the /healthz endpoint. Two checks:
///
///   db_ping         — a fresh DbContext executes <c>SELECT 1</c>. SQLite
///                     WAL mode lets this complete even while a catalog
///                     ingestion holds the writer, so this probe is safe
///                     during long imports.
///
///   settings_loaded — confirms <c>ISettingsService.Current</c> returns a
///                     populated snapshot. A blank <c>AuthUsername</c> would
///                     mean either the DB row is missing or the
///                     DataProtection key ring failed to decrypt the
///                     persisted settings — both fatal at the application
///                     level; better to fail-fast here so Fly can restart
///                     the machine than to serve traffic against broken
///                     auth.
///
/// Each check has its own narrow exception handling so that a fault in one
/// is reported independently in the response body. The endpoint enforces a
/// hard overall budget (2 s) via a linked CancellationTokenSource.
/// </summary>
public sealed class HealthCheckService
{
    private readonly IDbContextFactory<HarvesterDbContext> _factory;
    private readonly ISettingsService _settings;

    public HealthCheckService(IDbContextFactory<HarvesterDbContext> factory, ISettingsService settings)
    {
        _factory = factory;
        _settings = settings;
    }

    public async Task<HealthReport> CheckAsync(CancellationToken ct)
    {
        var checks = new Dictionary<string, HealthCheckResult>(StringComparer.Ordinal)
        {
            ["db_ping"] = await CheckDbAsync(ct),
            ["settings_loaded"] = CheckSettings(),
        };
        var ok = checks.Values.All(c => c.Ok);
        return new HealthReport(ok, checks);
    }

    private async Task<HealthCheckResult> CheckDbAsync(CancellationToken ct)
    {
        try
        {
            await using var db = await _factory.CreateDbContextAsync(ct);
            var rows = await db.Database.SqlQueryRaw<int>("SELECT 1 AS Value").ToListAsync(ct);
            if (rows.Count == 1 && rows[0] == 1)
                return HealthCheckResult.Pass();
            return HealthCheckResult.Fail($"unexpected response: {rows.Count} rows");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return HealthCheckResult.Fail("timed out");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Fail(ex.GetType().Name + ": " + ex.Message);
        }
    }

    private HealthCheckResult CheckSettings()
    {
        try
        {
            var s = _settings.Current;
            // AuthUsername is always seeded (default 'admin'); a blank value
            // means LoadAsync never ran or returned an empty entity, which
            // would be invisible at the API surface but lethal at login time.
            if (string.IsNullOrEmpty(s.AuthUsername))
                return HealthCheckResult.Fail("settings snapshot is empty");
            return HealthCheckResult.Pass();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Fail(ex.GetType().Name + ": " + ex.Message);
        }
    }
}

public sealed record HealthReport(bool Ok, IReadOnlyDictionary<string, HealthCheckResult> Checks);

public sealed record HealthCheckResult(bool Ok, string? Detail)
{
    public static HealthCheckResult Pass() => new(true, null);
    public static HealthCheckResult Fail(string detail) => new(false, detail);
}
