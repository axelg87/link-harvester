namespace LinkHarvester.Persistence;

/// <summary>
/// Single-row table holding all app settings the user configures from the
/// website. The Id is fixed to 1.
/// </summary>
public class AppSettingsEntity
{
    public int Id { get; set; } = 1;

    // Synology DownloadStation
    public string SynologyBaseUrl { get; set; } = string.Empty;
    public int SynologyConnectionMode { get; set; }
    public string SynologyQuickConnectId { get; set; } = string.Empty;
    public string SynologyResolvedBaseUrl { get; set; } = string.Empty;
    public DateTimeOffset? SynologyResolvedAt { get; set; }
    public string SynologyUsername { get; set; } = string.Empty;
    /// <summary>Stored as a DataProtection-encrypted ciphertext.</summary>
    public string SynologyPasswordEncrypted { get; set; } = string.Empty;
    public string? SynologyOtpCode { get; set; }
    public string SynologyMovieDestination { get; set; } = "video/movies";
    public string SynologySeriesDestination { get; set; } = "video/series";
    public string SynologyAnimeDestination { get; set; } = "video/anime";

    // Harvester
    public int ScanIntervalMinutes { get; set; } = 30;
    public bool ScanOnStartup { get; set; } = true;
    /// <summary>Comma-separated ordered hoster priority list.</summary>
    public string HosterPriorityCsv { get; set; } = "1fichier,Rapidgator";
    /// <summary>Comma-separated ordered quality preference (e.g. REMUX,BLURAY,WEB-DL,WEBRIP).</summary>
    public string QualityPreferenceCsv { get; set; } = "REMUX,BLURAY,WEB-DL,WEBRIP,HDTV";
    /// <summary>Preferred audio variant token used for "best variant" picking. One of MULTI, VFF, VFI, VOSTFR, VO.</summary>
    public string AudioPreference { get; set; } = "MULTI";

    // Auth
    public string AuthUsername { get; set; } = "admin";
    public string AuthPasswordEncrypted { get; set; } = string.Empty;

    // Catalog enrichment
    public string TmdbApiKeyEncrypted { get; set; } = string.Empty;
    public bool TmdbEnrichmentEnabled { get; set; } = true;
    public int TmdbEnrichmentConcurrency { get; set; } = 4;

    // Plex (optional movie-inventory source)
    public string PlexBaseUrl { get; set; } = string.Empty;
    public string PlexTokenEncrypted { get; set; } = string.Empty;

    // Discovery / Trakt (optional popularity source)
    public string TraktClientIdEncrypted { get; set; } = string.Empty;

    // Telegram bot (optional out-of-band channel)
    public string TelegramBotTokenEncrypted { get; set; } = string.Empty;
    /// <summary>Sole authorised chat. Bot drops updates from any other chat. Set via /start.</summary>
    public long TelegramOwnerChatId { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    // Read-model bookkeeping. CardBackfillService stamps this once the
    // initial rebuild from base tables has finished; the v2 endpoints
    // refuse to serve until it's set, so a partial backfill can never
    // surface as a half-populated inbox.
    public DateTimeOffset? CardsBackfilledAt { get; set; }
}
