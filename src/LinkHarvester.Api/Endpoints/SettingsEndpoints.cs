using LinkHarvester.Core;
using LinkHarvester.Synology;

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
                SynologyConnectionMode: c.SynologyConnectionMode.ToString(),
                SynologyQuickConnectId: c.SynologyQuickConnectId,
                SynologyResolvedBaseUrl: c.SynologyResolvedBaseUrl,
                SynologyResolvedAt: c.SynologyResolvedAt,
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
            var requestedMode = ParseSynologyConnectionMode(req.SynologyConnectionMode)
                ?? current.SynologyConnectionMode;
            var requestedQuickConnectId = req.SynologyQuickConnectId ?? current.SynologyQuickConnectId;
            var quickConnectIdentityChanged = requestedMode != current.SynologyConnectionMode
                || !string.Equals(requestedQuickConnectId, current.SynologyQuickConnectId, StringComparison.OrdinalIgnoreCase);
            // Empty password fields mean "leave unchanged".
            var snapshot = current with
            {
                SynologyBaseUrl = req.SynologyBaseUrl ?? current.SynologyBaseUrl,
                SynologyConnectionMode = requestedMode,
                SynologyQuickConnectId = requestedQuickConnectId,
                SynologyResolvedBaseUrl = quickConnectIdentityChanged ? string.Empty : current.SynologyResolvedBaseUrl,
                SynologyResolvedAt = quickConnectIdentityChanged ? null : current.SynologyResolvedAt,
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

        grp.MapPost("/resolve-quickconnect", async (ResolveQuickConnectDto req, IQuickConnectEndpointService endpoints, CancellationToken ct) =>
        {
            try
            {
                var result = await endpoints.RefreshAsync(req.QuickConnectId, ct);
                return Results.Ok(new QuickConnectResolveResult(
                    Ok: true,
                    BaseUrl: result.BaseUrl,
                    ResolvedAt: result.ResolvedAt,
                    ProbedUrls: result.ProbedUrls.ToList(),
                    Error: null));
            }
            catch (QuickConnectResolveException ex)
            {
                return Results.Ok(new QuickConnectResolveResult(false, null, null, new(), ex.Message));
            }
            catch (Exception ex)
            {
                return Results.Ok(new QuickConnectResolveResult(false, null, null, new(), ex.Message));
            }
        });

        grp.MapPost("/test-synology", async (IDownloadStationClient dsm, CancellationToken ct) =>
        {
            try
            {
                // Probing the auth flow via an empty URL list short-circuits before submission.
                await dsm.CreateTasksAsync(Array.Empty<string>(), null, ct);
                return Results.Ok(new { ok = true });
            }
            catch (DsmException dx)
            {
                return Results.Ok(new { ok = false, error = dx.HumanMessage });
            }
            catch (Exception ex)
            {
                return Results.Ok(new { ok = false, error = ex.Message });
            }
        });

        return routes;
    }

    private static SynologyConnectionMode? ParseSynologyConnectionMode(string? value)
        => Enum.TryParse<SynologyConnectionMode>(value, ignoreCase: true, out var mode)
            ? mode
            : null;

    public sealed record SettingsDto(
        string SynologyBaseUrl, string SynologyConnectionMode, string SynologyQuickConnectId,
        string SynologyResolvedBaseUrl, DateTimeOffset? SynologyResolvedAt,
        string SynologyUsername, bool SynologyPasswordSet,
        string? SynologyOtpCode, string SynologyMovieDestination, string SynologySeriesDestination,
        int ScanIntervalMinutes, bool ScanOnStartup, List<string> HosterPriority,
        string AuthUsername, bool AuthPasswordSet,
        bool TmdbApiKeySet, bool TmdbEnrichmentEnabled, int TmdbEnrichmentConcurrency);

    public sealed record UpdateSettingsDto(
        string? SynologyBaseUrl, string? SynologyConnectionMode, string? SynologyQuickConnectId,
        string? SynologyUsername, string? SynologyPassword,
        string? SynologyOtpCode, string? SynologyMovieDestination, string? SynologySeriesDestination,
        int? ScanIntervalMinutes, bool? ScanOnStartup, List<string>? HosterPriority,
        string? AuthUsername, string? AuthPassword,
        string? TmdbApiKey, bool? TmdbEnrichmentEnabled, int? TmdbEnrichmentConcurrency);

    public sealed record ResolveQuickConnectDto(string? QuickConnectId);

    public sealed record QuickConnectResolveResult(
        bool Ok,
        string? BaseUrl,
        DateTimeOffset? ResolvedAt,
        List<string> ProbedUrls,
        string? Error);
}
