namespace LinkHarvester.Core;

/// <summary>
/// Immutable plaintext view of all user-editable application settings.
/// Held in memory by ISettingsService and returned by GetCurrent().
/// </summary>
public sealed record AppSettingsSnapshot(
    // Synology
    string SynologyBaseUrl,
    string SynologyUsername,
    string SynologyPassword,
    string? SynologyOtpCode,
    string SynologyMovieDestination,
    string SynologySeriesDestination,
    string SynologyAnimeDestination,
    // Harvester
    int ScanIntervalMinutes,
    bool ScanOnStartup,
    IReadOnlyList<string> HosterPriority,
    // Auth
    string AuthUsername,
    string AuthPassword,
    // TMDB enrichment
    string TmdbApiKey,
    bool TmdbEnrichmentEnabled,
    int TmdbEnrichmentConcurrency,
    SynologyConnectionMode SynologyConnectionMode = SynologyConnectionMode.Direct,
    string SynologyQuickConnectId = "",
    string SynologyResolvedBaseUrl = "",
    DateTimeOffset? SynologyResolvedAt = null)
{
    public string EffectiveSynologyBaseUrl =>
        SynologyConnectionMode == SynologyConnectionMode.QuickConnect
        && !string.IsNullOrWhiteSpace(SynologyResolvedBaseUrl)
            ? SynologyResolvedBaseUrl
            : SynologyBaseUrl;
}

public interface ISettingsService
{
    AppSettingsSnapshot Current { get; }
    Task LoadAsync(CancellationToken ct);
    Task UpdateAsync(AppSettingsSnapshot updated, CancellationToken ct);
    /// <summary>Verifies a username + plaintext password against stored values.</summary>
    bool VerifyCredentials(string username, string password);
    event Action? Changed;
}
