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
    public string SynologyUsername { get; set; } = string.Empty;
    /// <summary>Stored as a DataProtection-encrypted ciphertext.</summary>
    public string SynologyPasswordEncrypted { get; set; } = string.Empty;
    public string? SynologyOtpCode { get; set; }
    public string SynologyMovieDestination { get; set; } = "video/movies";
    public string SynologySeriesDestination { get; set; } = "video/series";

    // Harvester
    public int ScanIntervalMinutes { get; set; } = 30;
    public bool ScanOnStartup { get; set; } = true;
    /// <summary>Comma-separated ordered hoster priority list.</summary>
    public string HosterPriorityCsv { get; set; } = "1fichier,Rapidgator";

    // Auth
    public string AuthUsername { get; set; } = "admin";
    public string AuthPasswordEncrypted { get; set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
