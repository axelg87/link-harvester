namespace LinkHarvester.Synology;

public sealed class SynologyOptions
{
    public string BaseUrl { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string? OtpCode { get; set; }
    public string? DefaultMovieDestination { get; set; }
    public string? DefaultSeriesDestination { get; set; }
    public int RequestTimeoutSeconds { get; set; } = 30;
}
