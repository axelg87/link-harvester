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
    int TmdbEnrichmentConcurrency);

public interface ISettingsService
{
    AppSettingsSnapshot Current { get; }
    Task LoadAsync(CancellationToken ct);
    Task UpdateAsync(AppSettingsSnapshot updated, CancellationToken ct);
    /// <summary>
    /// Runs <paramref name="action"/> while <see cref="Current"/> is temporarily
    /// replaced with <paramref name="snapshot"/> (not persisted). Restores the
    /// previous snapshot afterward and raises <see cref="Changed"/>.
    /// </summary>
    Task<T> WithSnapshotAsync<T>(AppSettingsSnapshot snapshot, Func<CancellationToken, Task<T>> action, CancellationToken ct);
    /// <summary>Verifies a username + plaintext password against stored values.</summary>
    bool VerifyCredentials(string username, string password);
    event Action? Changed;
}
