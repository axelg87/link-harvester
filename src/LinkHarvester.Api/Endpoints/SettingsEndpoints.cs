using LinkHarvester.Core;

namespace LinkHarvester.Api.Endpoints;

public static class SettingsEndpoints
{
    public static IEndpointRouteBuilder MapSettingsEndpoints(this IEndpointRouteBuilder routes)
    {
        var grp = routes.MapGroup("/api/settings").RequireAuthorization();

        grp.MapGet("", (ISettingsService s) =>
        {
            var c = s.Current;
            return Results.Ok(new SettingsDto(
                SynologyBaseUrl: c.SynologyBaseUrl,
                SynologyUsername: c.SynologyUsername,
                SynologyPasswordSet: !string.IsNullOrEmpty(c.SynologyPassword),
                SynologyOtpCode: c.SynologyOtpCode,
                SynologyMovieDestination: c.SynologyMovieDestination,
                SynologySeriesDestination: c.SynologySeriesDestination,
                ScanIntervalMinutes: c.ScanIntervalMinutes,
                ScanOnStartup: c.ScanOnStartup,
                HosterPriority: c.HosterPriority.ToList(),
                AuthUsername: c.AuthUsername,
                AuthPasswordSet: !string.IsNullOrEmpty(c.AuthPassword),
                TmdbApiKeySet: !string.IsNullOrEmpty(c.TmdbApiKey),
                TmdbEnrichmentEnabled: c.TmdbEnrichmentEnabled,
                TmdbEnrichmentConcurrency: c.TmdbEnrichmentConcurrency
            ));
        });

        grp.MapPut("", async (UpdateSettingsDto req, ISettingsService s, CancellationToken ct) =>
        {
            var current = s.Current;
            // Empty password fields mean "leave unchanged".
            var snapshot = current with
            {
                SynologyBaseUrl = req.SynologyBaseUrl ?? current.SynologyBaseUrl,
                SynologyUsername = req.SynologyUsername ?? current.SynologyUsername,
                SynologyPassword = string.IsNullOrEmpty(req.SynologyPassword) ? current.SynologyPassword : req.SynologyPassword,
                SynologyOtpCode = req.SynologyOtpCode,
                SynologyMovieDestination = req.SynologyMovieDestination ?? current.SynologyMovieDestination,
                SynologySeriesDestination = req.SynologySeriesDestination ?? current.SynologySeriesDestination,
                ScanIntervalMinutes = req.ScanIntervalMinutes ?? current.ScanIntervalMinutes,
                ScanOnStartup = req.ScanOnStartup ?? current.ScanOnStartup,
                HosterPriority = (req.HosterPriority is { Count: > 0 } ? req.HosterPriority : current.HosterPriority.ToList()),
                AuthUsername = req.AuthUsername ?? current.AuthUsername,
                AuthPassword = string.IsNullOrEmpty(req.AuthPassword) ? current.AuthPassword : req.AuthPassword,
                TmdbApiKey = string.IsNullOrEmpty(req.TmdbApiKey) ? current.TmdbApiKey : req.TmdbApiKey,
                TmdbEnrichmentEnabled = req.TmdbEnrichmentEnabled ?? current.TmdbEnrichmentEnabled,
                TmdbEnrichmentConcurrency = req.TmdbEnrichmentConcurrency ?? current.TmdbEnrichmentConcurrency
            };
            await s.UpdateAsync(snapshot, ct);
            return Results.Ok();
        });

        grp.MapPost("/test-synology", async (IDownloadStationClient dsm, CancellationToken ct) =>
        {
            try
            {
                // Probing the auth flow via an empty URL list short-circuits before submission.
                await dsm.CreateTasksAsync(Array.Empty<string>(), null, ct);
                return Results.Ok(new { ok = true });
            }
            catch (Exception ex)
            {
                return Results.Ok(new { ok = false, error = ex.Message });
            }
        });

        return routes;
    }

    public sealed record SettingsDto(
        string SynologyBaseUrl, string SynologyUsername, bool SynologyPasswordSet,
        string? SynologyOtpCode, string SynologyMovieDestination, string SynologySeriesDestination,
        int ScanIntervalMinutes, bool ScanOnStartup, List<string> HosterPriority,
        string AuthUsername, bool AuthPasswordSet,
        bool TmdbApiKeySet, bool TmdbEnrichmentEnabled, int TmdbEnrichmentConcurrency);

    public sealed record UpdateSettingsDto(
        string? SynologyBaseUrl, string? SynologyUsername, string? SynologyPassword,
        string? SynologyOtpCode, string? SynologyMovieDestination, string? SynologySeriesDestination,
        int? ScanIntervalMinutes, bool? ScanOnStartup, List<string>? HosterPriority,
        string? AuthUsername, string? AuthPassword,
        string? TmdbApiKey, bool? TmdbEnrichmentEnabled, int? TmdbEnrichmentConcurrency);
}
