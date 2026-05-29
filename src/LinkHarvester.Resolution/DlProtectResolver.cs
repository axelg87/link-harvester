using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using LinkHarvester.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LinkHarvester.Resolution;

/// <summary>
/// Resolves a single dl-protect.link URL into the final hoster URL.
///
/// dl-protect's flow is, in practice (verified empirically):
///   1. GET the page  -> server sets PHPSESSID cookie, page shows a Cloudflare
///      Turnstile widget and a disabled "Continuer" submit button.
///   2. POST to the same URL with `subform=unlock`. As long as we send a
///      session cookie, the response body contains the final hoster URL inside
///      &lt;a class="dest-url" href="..."&gt; and &lt;a class="btn-proceed" href="..."&gt;.
///      The Turnstile token is NOT validated server-side at the moment;
///      sending an empty / arbitrary `cf-turnstile-response` is accepted.
///
/// If dl-protect ever starts enforcing the Turnstile token, the
/// CapSolver fallback is wired in but normally unused.
/// </summary>
public sealed class DlProtectResolver : ILinkResolver
{
    private static readonly string[] FinalHosterDomainHints =
    {
        "1fichier.com", "rapidgator.net", "uploady.io", "dailyuploads.net", "nitroflare.com", "turbobit.net"
    };

    private readonly IHttpClientFactory _httpFactory;
    private readonly CapSolverClient _capSolver;
    private readonly ICapSolverBudget _budget;
    private readonly ResolverOptions _opts;
    private readonly ILogger<DlProtectResolver> _log;

    public DlProtectResolver(IHttpClientFactory httpFactory,
                             CapSolverClient capSolver,
                             ICapSolverBudget budget,
                             IOptions<ResolverOptions> opts,
                             ILogger<DlProtectResolver> log)
    {
        _httpFactory = httpFactory;
        _capSolver = capSolver;
        _budget = budget;
        _opts = opts.Value;
        _log = log;
    }

    public async Task<ResolutionOutcome> ResolveAsync(string protectedUrl, CancellationToken ct)
    {
        var capCalls = 0;
        decimal capCost = 0m;

        for (var attempt = 1; attempt <= _opts.MaxAttemptsPerLink; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var client = _httpFactory.CreateClient(NamedClients.DlProtect);
                var cookies = new System.Net.CookieContainer();
                using var handler = new HttpClientHandler
                {
                    CookieContainer = cookies,
                    AllowAutoRedirect = true,
                    UseCookies = true
                };
                using var http = new HttpClient(handler)
                {
                    Timeout = TimeSpan.FromSeconds(_opts.OverallTimeoutSeconds)
                };
                http.DefaultRequestHeaders.UserAgent.ParseAdd(
                    "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/121.0 Safari/537.36");
                http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("fr-FR,fr;q=0.9,en;q=0.8");
                http.DefaultRequestHeaders.Add("Referer", "https://www.zone-telechargement.news/");

                using (var getResp = await http.GetAsync(protectedUrl, ct))
                {
                    if (!getResp.IsSuccessStatusCode)
                    {
                        _log.LogWarning("dl-protect GET returned {Code}", (int)getResp.StatusCode);
                        continue;
                    }
                }

                string? token = null;
                if (_capSolver.IsConfigured && await _budget.CanSolveAsync(ct))
                {
                    var siteKey = await TryReadTurnstileSiteKeyAsync(http, protectedUrl, ct);
                    if (!string.IsNullOrEmpty(siteKey))
                    {
                        token = await _capSolver.SolveTurnstileAsync(protectedUrl, siteKey!, ct);
                        if (token is not null)
                        {
                            capCalls++;
                            capCost += 0.0008m;
                            await _budget.RecordSolveAsync(0.0008m, ct);
                        }
                    }
                }

                var form = new List<KeyValuePair<string, string>>
                {
                    new("subform", "unlock"),
                    new("cf-turnstile-response", token ?? "invalid")
                };
                using var postContent = new FormUrlEncodedContent(form);
                using var postResp = await http.PostAsync(protectedUrl, postContent, ct);
                if (!postResp.IsSuccessStatusCode)
                {
                    _log.LogWarning("dl-protect POST returned {Code}", (int)postResp.StatusCode);
                    continue;
                }
                var body = await postResp.Content.ReadAsStringAsync(ct);
                var finalUrls = ExtractFinalUrls(body);
                if (finalUrls.Count > 0)
                {
                    var links = finalUrls
                        .Select(u => new ResolvedLink(GuessHoster(u), u, null))
                        .ToList();
                    return new ResolutionOutcome(ResolutionAttemptResult.Success, links, null, capCalls, capCost);
                }

                // Structured diagnostic — when dl-protect changes its HTML
                // we get a silent "no URLs extracted" failure with no clue
                // why. Capture everything a human or a script needs to tell
                // the three plausible regressions apart without a debugger:
                //   (a) selectors changed: tokenSent=false/true but body has
                //       no a.dest-url / a.btn-proceed match + body large.
                //   (b) Turnstile now enforced: tokenSent=false, body still
                //       shows the challenge widget on the POST response.
                //   (c) link genuinely dead: short body, error markup.
                _log.LogWarning(
                    "dl-protect POST yielded no hoster URL. attempt={Attempt} url={Url} bodyLen={BodyLen} " +
                    "hasDestUrlAnchor={HasDest} hasBtnProceedAnchor={HasBtn} " +
                    "hasTurnstileMarkup={HasTurnstile} tokenSent={TokenSent} bodyHead={BodyHead}",
                    attempt, protectedUrl, body.Length,
                    body.Contains("dest-url", StringComparison.OrdinalIgnoreCase),
                    body.Contains("btn-proceed", StringComparison.OrdinalIgnoreCase),
                    body.Contains("cf-turnstile", StringComparison.OrdinalIgnoreCase)
                        || body.Contains("data-sitekey", StringComparison.OrdinalIgnoreCase),
                    token is { Length: > 0 },
                    Truncate(body, 240).Replace('\n', ' ').Replace('\r', ' '));
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                _log.LogWarning("dl-protect request timed out (attempt {N})", attempt);
            }
            catch (HttpRequestException hex)
            {
                _log.LogWarning(hex, "dl-protect HTTP error on attempt {N}", attempt);
            }
        }

        return new ResolutionOutcome(
            ResolutionAttemptResult.NoLinksFound,
            Array.Empty<ResolvedLink>(),
            "Could not extract any final hoster URL after all attempts",
            capCalls, capCost);
    }

    private static List<string> ExtractFinalUrls(string body)
    {
        var doc = new HtmlParser().ParseDocument(body);
        var urls = new List<string>();

        // Preferred anchor: explicit class.
        foreach (var a in doc.QuerySelectorAll("a.dest-url, a.btn-proceed").OfType<IHtmlAnchorElement>())
        {
            var href = a.GetAttribute("href");
            if (!string.IsNullOrEmpty(href) && IsFinalHosterUrl(href))
                urls.Add(href!);
        }

        // Fallback: any <a> on the page that points at a known hoster domain.
        if (urls.Count == 0)
        {
            foreach (var a in doc.QuerySelectorAll("a[href]").OfType<IHtmlAnchorElement>())
            {
                var href = a.GetAttribute("href") ?? string.Empty;
                if (IsFinalHosterUrl(href)) urls.Add(href);
            }
        }

        return urls.Distinct().ToList();
    }

    private static bool IsFinalHosterUrl(string href)
    {
        if (string.IsNullOrEmpty(href)) return false;
        if (!href.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return false;
        if (href.Contains("dl-protect.link", StringComparison.OrdinalIgnoreCase)) return false;
        return FinalHosterDomainHints.Any(d => href.Contains(d, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<string?> TryReadTurnstileSiteKeyAsync(HttpClient http, string url, CancellationToken ct)
    {
        try
        {
            var html = await http.GetStringAsync(url, ct);
            var m = System.Text.RegularExpressions.Regex.Match(html,
                "data-sitekey=[\"'](?<k>[A-Za-z0-9_\\-]+)[\"']");
            return m.Success ? m.Groups["k"].Value : null;
        }
        catch { return null; }
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s.Substring(0, max);

    private static string GuessHoster(string url)
    {
        var u = url.ToLowerInvariant();
        if (u.Contains("1fichier.com")) return "1fichier";
        if (u.Contains("rapidgator.net")) return "Rapidgator";
        if (u.Contains("uploady.io")) return "Uploady";
        if (u.Contains("dailyuploads.net")) return "DailyUploads";
        if (u.Contains("nitroflare.com")) return "Nitroflare";
        if (u.Contains("turbobit.net")) return "Turbobit";
        return "Unknown";
    }
}
