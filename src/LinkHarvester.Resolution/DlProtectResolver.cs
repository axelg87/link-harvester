using System.Text.RegularExpressions;
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
    // Substring match — covers subdomain variants (rg.to vs rapidgator.net,
    // dl.1fichier.com, etc.). Add a new host as you observe it landing in
    // the body. ".com" / ".net" suffix variants both appear in the wild;
    // dl-protect rotates the hoster set every few weeks.
    private static readonly string[] FinalHosterDomainHints =
    {
        "1fichier.com", "1fichier.net",
        "rapidgator.net", "rapidgator.com", "rg.to",
        "uploady.io", "uploady.cc",
        "dailyuploads.net",
        "nitroflare.com", "nitroflare.net",
        "turbobit.net", "turb.cc",
        "mega.nz", "mega.co.nz",
        "fikper.com",
        "katfile.com",
        "ddownload.com", "ddl.to",
        "uploadgig.com",
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

                // Structured diagnostic. PR #36 showed the next layer of the
                // mystery: hasDestUrlAnchor=True yet destAnchorOuterHtml=<none>
                // — the literal string "dest-url" is in the body, but it's
                // not on an <a class="dest-url"> element. And Layer 3
                // (body-text regex for known hoster URLs) found nothing
                // either. So either the POST is being silently rejected
                // (we get the GET page back), or the URL is delivered via
                // a JS-driven XHR after page-load. Two extra dumps to tell
                // those apart on the next failure:
                //   destUrlContexts — every 240-char window around the
                //     substring "dest-url" (so we can see whether it's in
                //     a script, on a button, in a comment, etc.).
                //   bodyTail — the LAST 800 chars of the body (the head
                //     is usually boilerplate; the action is at the bottom).
                // bodyHead is also bumped from 240 → 800 chars.
                var destAnchorDump = DumpDestAnchors(body);
                var destUrlContexts = ExtractKeywordContexts(body, "dest-url", windowChars: 120, maxHits: 4);
                _log.LogWarning(
                    "dl-protect POST yielded no hoster URL. attempt={Attempt} url={Url} bodyLen={BodyLen} " +
                    "hasDestUrlAnchor={HasDest} hasBtnProceedAnchor={HasBtn} " +
                    "hasTurnstileMarkup={HasTurnstile} tokenSent={TokenSent} " +
                    "destAnchorOuterHtml={DestAnchorHtml} destUrlContexts={DestUrlContexts} " +
                    "bodyHead={BodyHead} bodyTail={BodyTail}",
                    attempt, protectedUrl, body.Length,
                    body.Contains("dest-url", StringComparison.OrdinalIgnoreCase),
                    body.Contains("btn-proceed", StringComparison.OrdinalIgnoreCase),
                    body.Contains("cf-turnstile", StringComparison.OrdinalIgnoreCase)
                        || body.Contains("data-sitekey", StringComparison.OrdinalIgnoreCase),
                    token is { Length: > 0 },
                    destAnchorDump,
                    destUrlContexts,
                    Truncate(body, 800).Replace('\n', ' ').Replace('\r', ' '),
                    TruncateTail(body, 800).Replace('\n', ' ').Replace('\r', ' '));
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

    /// <summary>Test seam — same algorithm as the private path.</summary>
    internal static List<string> ExtractFinalUrlsForTesting(string body) => ExtractFinalUrls(body);

    private static List<string> ExtractFinalUrls(string body)
    {
        var doc = new HtmlParser().ParseDocument(body);
        var urls = new List<string>();

        // Layer 1 — preferred anchor classes (legacy dl-protect format).
        foreach (var a in doc.QuerySelectorAll("a.dest-url, a.btn-proceed").OfType<IHtmlAnchorElement>())
        {
            HarvestCandidate(a.GetAttribute("href"), urls);
            // The structured PR #35 diagnostic showed `hasDestUrlAnchor=True`
            // on every recent failure, yet IsFinalHosterUrl rejected the
            // href. dl-protect now (sometimes) puts the real URL on a data
            // attribute and uses `href="#"` or a relative path on the
            // anchor itself — pick those up too.
            HarvestCandidate(a.GetAttribute("data-href"), urls);
            HarvestCandidate(a.GetAttribute("data-link"), urls);
            HarvestCandidate(a.GetAttribute("data-url"), urls);
        }

        // Layer 2 — fallback: any <a>/<button> with a href OR a data-href
        // pointing at a known hoster domain.
        if (urls.Count == 0)
        {
            foreach (var el in doc.QuerySelectorAll("a[href], a[data-href], a[data-url], button[data-href], button[data-url]"))
            {
                HarvestCandidate(el.GetAttribute("href"), urls);
                HarvestCandidate(el.GetAttribute("data-href"), urls);
                HarvestCandidate(el.GetAttribute("data-link"), urls);
                HarvestCandidate(el.GetAttribute("data-url"), urls);
            }
        }

        // Layer 3 — body-text regex. Catches URLs embedded in inline JS
        // (`window.location='https://1fichier.com/…'`, `onclick="…"`, etc.)
        // and anywhere outside an anchor element. Last-resort, deliberately
        // permissive: must be https://, must hit a known hoster domain.
        if (urls.Count == 0)
        {
            foreach (Match m in HosterUrlInBodyRegex.Matches(body))
            {
                HarvestCandidate(m.Value, urls);
            }
        }

        return urls.Distinct().ToList();
    }

    private static void HarvestCandidate(string? raw, List<string> sink)
    {
        if (string.IsNullOrWhiteSpace(raw)) return;
        var trimmed = raw.Trim().Trim('"', '\'');
        if (IsFinalHosterUrl(trimmed)) sink.Add(trimmed);
    }

    private static readonly Regex HosterUrlInBodyRegex = new(
        @"https://[A-Za-z0-9._\-/?&=%~+#]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

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

    private static string TruncateTail(string s, int max)
        => s.Length <= max ? s : s.Substring(s.Length - max);

    /// <summary>
    /// Find every occurrence of <paramref name="needle"/> in <paramref name="haystack"/>
    /// and emit a <paramref name="windowChars"/>-wide slice on either side.
    /// Joined with " ┊ ", capped at <paramref name="maxHits"/>. Used to see
    /// whether a substring (e.g. "dest-url", "1fichier") lives in inline
    /// JS, a class name, a comment, etc., without dumping the full body.
    /// </summary>
    private static string ExtractKeywordContexts(string haystack, string needle, int windowChars, int maxHits)
    {
        if (string.IsNullOrEmpty(haystack) || string.IsNullOrEmpty(needle)) return "<empty>";
        var hits = new List<string>(maxHits);
        var idx = 0;
        while (hits.Count < maxHits)
        {
            var found = haystack.IndexOf(needle, idx, StringComparison.OrdinalIgnoreCase);
            if (found < 0) break;
            var start = Math.Max(0, found - windowChars);
            var end = Math.Min(haystack.Length, found + needle.Length + windowChars);
            hits.Add(haystack.Substring(start, end - start)
                .Replace('\n', ' ').Replace('\r', ' '));
            idx = found + needle.Length;
        }
        return hits.Count == 0 ? "<not-found>" : string.Join(" ┊ ", hits);
    }

    /// <summary>
    /// Returns the outer HTML of every <c>a.dest-url</c> / <c>a.btn-proceed</c>
    /// element on the page, joined with " ┊ " and truncated to 480 chars.
    /// If extraction still fails after the data-attr + body-regex layers
    /// in <see cref="ExtractFinalUrls"/>, this line tells us the exact
    /// shape dl-protect is serving so we can patch surgically.
    /// </summary>
    private static string DumpDestAnchors(string body)
    {
        try
        {
            var doc = new HtmlParser().ParseDocument(body);
            var anchors = doc.QuerySelectorAll("a.dest-url, a.btn-proceed").OfType<IHtmlAnchorElement>().ToList();
            if (anchors.Count == 0) return "<none>";
            var joined = string.Join(" ┊ ",
                anchors.Select(a => (a.OuterHtml ?? string.Empty).Replace('\n', ' ').Replace('\r', ' ')));
            return Truncate(joined, 480);
        }
        catch { return "<parse-error>"; }
    }

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
