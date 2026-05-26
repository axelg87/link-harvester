using System.Text.RegularExpressions;
using LinkHarvester.Resolution.HealthCheck.Hosters;
using Microsoft.Extensions.Logging;

namespace LinkHarvester.Resolution.HealthCheck;

/// <summary>
/// Result of probing one hoster URL.
/// </summary>
public enum LinkHealth
{
    /// <summary>The probe positively indicated the file exists (HEAD/GET succeeded
    /// with a hoster-specific "alive" signature).</summary>
    Alive,

    /// <summary>The probe matched a hoster-specific dead signature
    /// (e.g. 404 + "file does not exist" body).</summary>
    Dead,

    /// <summary>The hoster blocked us (anti-bot, Cloudflare interstitial, TCP reset),
    /// or we don't have a per-host probe strategy for this URL. Default to
    /// treating as Alive for hide-filter purposes.</summary>
    Unknown,
}

public sealed record HealthCheckResult(
    LinkHealth Health,
    int? StatusCode,
    string? Title,
    string? Signature,
    TimeSpan Duration);

public interface ILinkHealthService
{
    /// <summary>
    /// Probe a single hoster URL and return its three-state health.
    /// </summary>
    Task<HealthCheckResult> CheckAsync(string url, string? hosterName, CancellationToken ct);

    /// <summary>
    /// Probe a list of URLs in order and short-circuit on the first
    /// <see cref="LinkHealth.Alive"/> or <see cref="LinkHealth.Unknown"/>.
    /// Returns the result of the first probe that wasn't <see cref="LinkHealth.Dead"/>,
    /// or the last Dead result if every URL was Dead.
    /// </summary>
    Task<HealthCheckResult> CheckUntilAliveAsync(
        IEnumerable<(string url, string? hoster)> candidates,
        CancellationToken ct);
}

public sealed class LinkHealthService : ILinkHealthService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<LinkHealthService> _log;
    private readonly IReadOnlyList<IHosterHealthMatcher> _matchers;
    private readonly IHosterHealthMatcher _fallback;

    public LinkHealthService(IHttpClientFactory httpFactory, ILogger<LinkHealthService> log)
        : this(httpFactory, log, HosterHealthMatcherRegistry.Default, new UnknownHosterMatcher())
    {
    }

    internal LinkHealthService(
        IHttpClientFactory httpFactory,
        ILogger<LinkHealthService> log,
        IReadOnlyList<IHosterHealthMatcher> matchers,
        IHosterHealthMatcher fallback)
    {
        _httpFactory = httpFactory;
        _log = log;
        _matchers = matchers;
        _fallback = fallback;
    }

    public async Task<HealthCheckResult> CheckAsync(string url, string? hosterName, CancellationToken ct)
    {
        var start = DateTimeOffset.UtcNow;
        var matcher = ResolveMatcher(url, hosterName);

        var client = _httpFactory.CreateClient(NamedClients.LinkHealth);

        HttpResponseMessage? response = null;
        string body = string.Empty;
        int? statusCode = null;
        Exception? error = null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Range", "bytes=0-3000");
            response = await client.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
            statusCode = (int)response.StatusCode;
            body = await SafeReadAsync(response, ct);
        }
        catch (HttpRequestException ex)
        {
            error = ex;
            _log.LogDebug(ex, "LinkHealthService transport error for {Url}", url);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            error = new TimeoutException("timeout");
        }
        finally
        {
            response?.Dispose();
        }

        var verdict = matcher.Evaluate(statusCode, body, error);
        var duration = DateTimeOffset.UtcNow - start;
        var title = ExtractTitle(body);
        var signature = $"{matcher.GetType().Name}|status={statusCode?.ToString() ?? "err"}";

        return new HealthCheckResult(verdict, statusCode, title, signature, duration);
    }

    public async Task<HealthCheckResult> CheckUntilAliveAsync(
        IEnumerable<(string url, string? hoster)> candidates,
        CancellationToken ct)
    {
        HealthCheckResult? last = null;
        foreach (var (url, hoster) in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var r = await CheckAsync(url, hoster, ct);
            last = r;
            if (r.Health != LinkHealth.Dead)
                return r;
        }
        return last ?? new HealthCheckResult(LinkHealth.Unknown, null, null, "no-candidates", TimeSpan.Zero);
    }

    private IHosterHealthMatcher ResolveMatcher(string url, string? hosterName)
    {
        foreach (var m in _matchers)
        {
            if (m.Matches(url, hosterName)) return m;
        }
        return _fallback;
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(8));
            var bytes = await resp.Content.ReadAsByteArrayAsync(cts.Token);
            // Limit to the first 32 KB to keep probes cheap.
            var len = Math.Min(bytes.Length, 32 * 1024);
            return System.Text.Encoding.UTF8.GetString(bytes, 0, len);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static readonly Regex TitleRegex = new(
        "<title[^>]*>(?<t>[^<]{1,300})</title>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static string? ExtractTitle(string body)
    {
        if (string.IsNullOrEmpty(body)) return null;
        var m = TitleRegex.Match(body);
        return m.Success ? m.Groups["t"].Value.Trim() : null;
    }
}
