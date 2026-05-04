using System.Text.RegularExpressions;
using LinkHarvester.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;

namespace LinkHarvester.Resolution;

/// <summary>
/// Resolves a single dl-protect.link URL into the final hoster URL using Playwright.
/// Strategy:
///   1. Open the page in a persistent Chromium context.
///   2. Wait for the page to settle. Turnstile typically self-solves invisibly.
///   3. Click any visible "Continuer" / "Continue" button if present.
///   4. After the protected content is revealed, capture either:
///        - the first hoster anchor on the page, OR
///        - a known redirect target.
///   5. If a Turnstile widget is detected and not auto-solved within a timeout,
///      defer to CapSolver (if configured + budget available).
/// </summary>
public sealed class DlProtectResolver : ILinkResolver
{
    private static readonly Regex TurnstileSiteKeyRegex =
        new(@"data-sitekey=[""'](?<k>[a-zA-Z0-9_\-]+)[""']", RegexOptions.Compiled);

    private static readonly string[] FinalHosterDomainHints =
    {
        "1fichier.com", "rapidgator.net", "uploady.io", "dailyuploads.net", "nitroflare.com"
    };

    private readonly PlaywrightInstaller _installer;
    private readonly CapSolverClient _capSolver;
    private readonly ICapSolverBudget _budget;
    private readonly ResolverOptions _opts;
    private readonly ILogger<DlProtectResolver> _log;

    public DlProtectResolver(PlaywrightInstaller installer,
                             CapSolverClient capSolver,
                             ICapSolverBudget budget,
                             IOptions<ResolverOptions> opts,
                             ILogger<DlProtectResolver> log)
    {
        _installer = installer;
        _capSolver = capSolver;
        _budget = budget;
        _opts = opts.Value;
        _log = log;
    }

    public async Task<ResolutionOutcome> ResolveAsync(string protectedUrl, CancellationToken ct)
    {
        await _installer.EnsureInstalledAsync(ct);

        Directory.CreateDirectory(_opts.PersistentProfileDirectory);

        using var pw = await Playwright.CreateAsync();
        var context = await pw.Chromium.LaunchPersistentContextAsync(
            _opts.PersistentProfileDirectory,
            new BrowserTypeLaunchPersistentContextOptions
            {
                Headless = !_opts.Headed,
                Locale = "fr-FR",
                ViewportSize = new ViewportSize { Width = 1280, Height = 800 },
                UserAgent = "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/121.0 Safari/537.36",
                Args = new[] { "--disable-blink-features=AutomationControlled" }
            });

        try
        {
            var page = await context.NewPageAsync();
            page.SetDefaultTimeout(_opts.OverallTimeoutSeconds * 1000);

            int capSolverCalls = 0;
            decimal capSolverCost = 0m;

            for (var attempt = 1; attempt <= _opts.MaxAttemptsPerLink; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                _log.LogInformation("Resolving {Url} attempt {N}", protectedUrl, attempt);
                try
                {
                    await page.GotoAsync(protectedUrl, new PageGotoOptions
                    {
                        WaitUntil = WaitUntilState.NetworkIdle,
                        Timeout = _opts.OverallTimeoutSeconds * 1000
                    });

                    // Light wait for Turnstile to self-solve.
                    await Task.Delay(2500, ct);

                    // Try clicking any visible "Continuer"/"Continue" button.
                    await TryClickContinueAsync(page);

                    var finalUrls = await CollectFinalUrlsAsync(page);
                    if (finalUrls.Count > 0)
                    {
                        var links = finalUrls.Select(u => new ResolvedLink(GuessHoster(u), u, null)).ToList();
                        return new ResolutionOutcome(ResolutionAttemptResult.Success, links, null,
                            capSolverCalls, capSolverCost);
                    }

                    // No links visible -> maybe Turnstile is blocking; try CapSolver if configured.
                    var html = await page.ContentAsync();
                    var siteKey = TurnstileSiteKeyRegex.Match(html).Groups["k"].Value;
                    if (!string.IsNullOrEmpty(siteKey)
                        && _capSolver.IsConfigured
                        && await _budget.CanSolveAsync(ct))
                    {
                        _log.LogInformation("Falling back to CapSolver Turnstile (siteKey {SK})", siteKey);
                        var token = await _capSolver.SolveTurnstileAsync(protectedUrl, siteKey, ct);
                        capSolverCalls++;
                        capSolverCost += 0.0008m;
                        await _budget.RecordSolveAsync(0.0008m, ct);

                        if (!string.IsNullOrEmpty(token))
                        {
                            await InjectTurnstileTokenAsync(page, token);
                            await Task.Delay(2000, ct);
                            await TryClickContinueAsync(page);
                            var afterCap = await CollectFinalUrlsAsync(page);
                            if (afterCap.Count > 0)
                            {
                                var links = afterCap.Select(u => new ResolvedLink(GuessHoster(u), u, null)).ToList();
                                return new ResolutionOutcome(ResolutionAttemptResult.Success, links, null,
                                    capSolverCalls, capSolverCost);
                            }
                        }
                    }
                }
                catch (TimeoutException tex)
                {
                    _log.LogWarning(tex, "Timeout on attempt {N}", attempt);
                }
                catch (PlaywrightException pex)
                {
                    _log.LogWarning(pex, "Playwright error on attempt {N}", attempt);
                }
            }

            return new ResolutionOutcome(ResolutionAttemptResult.BotDetected,
                Array.Empty<ResolvedLink>(),
                "Could not extract any final hoster URL after all attempts",
                capSolverCalls, capSolverCost);
        }
        finally
        {
            await context.CloseAsync();
        }
    }

    private static async Task TryClickContinueAsync(IPage page)
    {
        // Order: most specific selectors first.
        string[] candidates = new[]
        {
            "button:has-text(\"Continuer\")",
            "button:has-text(\"Continue\")",
            "a:has-text(\"Continuer\")",
            "a:has-text(\"Continue\")",
            "input[type=submit][value*=\"Continuer\" i]"
        };

        foreach (var sel in candidates)
        {
            try
            {
                var loc = page.Locator(sel);
                if (await loc.CountAsync() > 0 && await loc.First.IsVisibleAsync())
                {
                    await loc.First.ClickAsync(new LocatorClickOptions { Timeout = 5000 });
                    return;
                }
            }
            catch { /* try next */ }
        }
    }

    private static async Task<List<string>> CollectFinalUrlsAsync(IPage page)
    {
        var urls = new List<string>();
        // dl-protect typically reveals an anchor with class "lien" or similar.
        var anchorHrefs = await page.EvalOnSelectorAllAsync<string[]>("a[href]",
            "els => els.map(e => e.href)");

        foreach (var href in anchorHrefs)
        {
            if (string.IsNullOrEmpty(href)) continue;
            if (FinalHosterDomainHints.Any(d => href.Contains(d, StringComparison.OrdinalIgnoreCase)))
            {
                urls.Add(href);
            }
        }

        // Also look for plain-text URLs the page may render in <input>/<code> blocks.
        var bodyText = await page.InnerTextAsync("body");
        foreach (var d in FinalHosterDomainHints)
        {
            foreach (Match m in Regex.Matches(bodyText, @"https?://[^\s""'<>]+" + Regex.Escape(d) + @"[^\s""'<>]*"))
            {
                urls.Add(m.Value);
            }
        }

        return urls.Distinct().ToList();
    }

    private static async Task InjectTurnstileTokenAsync(IPage page, string token)
    {
        // Set the cf-turnstile-response value used by dl-protect's form, then dispatch
        // any onchange handlers it may rely on.
        await page.EvaluateAsync(@"(t) => {
            const inputs = document.querySelectorAll('input[name=cf-turnstile-response], input[name=g-recaptcha-response]');
            inputs.forEach(i => { i.value = t; i.dispatchEvent(new Event('change', { bubbles: true })); });
        }", token);
    }

    private static string GuessHoster(string url)
    {
        var u = url.ToLowerInvariant();
        if (u.Contains("1fichier.com")) return "1fichier";
        if (u.Contains("rapidgator.net")) return "Rapidgator";
        if (u.Contains("uploady.io")) return "Uploady";
        if (u.Contains("dailyuploads.net")) return "DailyUploads";
        if (u.Contains("nitroflare.com")) return "Nitroflare";
        return "Unknown";
    }
}
