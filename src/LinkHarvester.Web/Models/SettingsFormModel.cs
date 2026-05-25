namespace LinkHarvester.Web.Models;

/// <summary>
/// EditForm-bound mirror of <see cref="Settings"/>. Mutated by the Settings
/// page's sub-components, then serialised back to <see cref="UpdateSettings"/>
/// on Save. Lives outside the Settings page itself so each per-section
/// component can take it as a <c>[Parameter]</c>.
/// </summary>
public sealed class SettingsFormModel
{
    public string SynologyBaseUrl { get; set; } = "";
    public string SynologyUsername { get; set; } = "";
    public string SynologyPassword { get; set; } = "";
    public string? SynologyOtpCode { get; set; }
    public string SynologyMovieDestination { get; set; } = "";
    public string SynologySeriesDestination { get; set; } = "";
    public int ScanIntervalMinutes { get; set; }
    public bool ScanOnStartup { get; set; }
    public List<string> HosterPriority { get; set; } = new();
    public string AuthUsername { get; set; } = "";
    public string AuthPassword { get; set; } = "";
    public string TmdbApiKey { get; set; } = "";
    public bool TmdbEnrichmentEnabled { get; set; } = true;
    public int TmdbEnrichmentConcurrency { get; set; } = 4;

    public static SettingsFormModel From(Settings s) => new()
    {
        SynologyBaseUrl = s.SynologyBaseUrl,
        SynologyUsername = s.SynologyUsername,
        SynologyPassword = "",
        SynologyOtpCode = s.SynologyOtpCode,
        SynologyMovieDestination = s.SynologyMovieDestination,
        SynologySeriesDestination = s.SynologySeriesDestination,
        ScanIntervalMinutes = s.ScanIntervalMinutes,
        ScanOnStartup = s.ScanOnStartup,
        HosterPriority = s.HosterPriority.ToList(),
        AuthUsername = s.AuthUsername,
        AuthPassword = "",
        TmdbApiKey = "",
        TmdbEnrichmentEnabled = s.TmdbEnrichmentEnabled,
        TmdbEnrichmentConcurrency = s.TmdbEnrichmentConcurrency
    };

    public UpdateSettings ToUpdate() => new(
        SynologyBaseUrl: SynologyBaseUrl,
        SynologyUsername: SynologyUsername,
        SynologyPassword: SynologyPassword,
        SynologyOtpCode: SynologyOtpCode,
        SynologyMovieDestination: SynologyMovieDestination,
        SynologySeriesDestination: SynologySeriesDestination,
        ScanIntervalMinutes: ScanIntervalMinutes,
        ScanOnStartup: ScanOnStartup,
        HosterPriority: HosterPriority.Where(h => !string.IsNullOrWhiteSpace(h)).ToList(),
        AuthUsername: AuthUsername,
        AuthPassword: AuthPassword,
        TmdbApiKey: TmdbApiKey,
        TmdbEnrichmentEnabled: TmdbEnrichmentEnabled,
        TmdbEnrichmentConcurrency: TmdbEnrichmentConcurrency);
}

/// <summary>
/// Counting-stream wrapper used by the catalog upload UI to surface
/// "X.X MB uploaded" without reading the whole stream into memory. Pulled
/// out of SettingsPage.razor so the per-section components can stay lean
/// (and other upload UIs can reuse it later).
/// </summary>
public sealed class CountingReadStream : Stream
{
    private readonly Stream _inner;
    private readonly Action<long> _onRead;

    public CountingReadStream(Stream inner, Action<long> onRead)
    {
        _inner = inner;
        _onRead = onRead;
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count)
    {
        var n = _inner.Read(buffer, offset, count);
        if (n > 0) _onRead(n);
        return n;
    }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        var n = await _inner.ReadAsync(buffer, ct);
        if (n > 0) _onRead(n);
        return n;
    }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
