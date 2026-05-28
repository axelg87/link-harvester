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
    DateTimeOffset? SynologyResolvedAt = null,
    // Plex — optional. When set, used to answer "is this movie on disk?"
    // for catalog items where the file lives flat in the movie root and
    // filename matching is unreliable. Token is encrypted at rest.
    string PlexBaseUrl = "",
    string PlexToken = "",
    // Best-variant picking — used by the universal search bar, Telegram bot,
    // and any future "auto-grab" path that needs a single deterministic pick.
    IReadOnlyList<string>? QualityPreference = null,
    string AudioPreference = "MULTI",
    // Discovery / Trakt — optional public-trending source.
    string TraktClientId = "",
    // Telegram bot — optional out-of-band command + notification channel.
    string TelegramBotToken = "",
    long TelegramOwnerChatId = 0)
{
    private static readonly IReadOnlyList<string> DefaultQualityPreference
        = new[] { "REMUX", "BLURAY", "WEB-DL", "WEBRIP", "HDTV" };

    public string EffectiveSynologyBaseUrl =>
        SynologyConnectionMode == SynologyConnectionMode.QuickConnect
        && !string.IsNullOrWhiteSpace(SynologyResolvedBaseUrl)
            ? SynologyResolvedBaseUrl
            : SynologyBaseUrl;

    /// <summary>Non-null view of QualityPreference for callers that don't want to
    /// branch on null. Falls back to the canonical default token list.</summary>
    public IReadOnlyList<string> EffectiveQualityPreference =>
        QualityPreference is { Count: > 0 } qp ? qp : DefaultQualityPreference;
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
