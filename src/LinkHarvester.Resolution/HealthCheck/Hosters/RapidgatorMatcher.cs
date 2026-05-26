namespace LinkHarvester.Resolution.HealthCheck.Hosters;

/// <summary>
/// Rapidgator: alive responses include the filename in the &lt;title&gt; tag
/// (e.g. "Télécharger le fichier ..."). Dead responses redirect to a
/// "Buy premium" / login page with no filename in the title.
/// </summary>
public sealed class RapidgatorMatcher : IHosterHealthMatcher
{
    public bool Matches(string url, string? hosterName) =>
        Contains(hosterName, "rapidgator") || Contains(url, "rapidgator.net");

    public LinkHealth Evaluate(int? statusCode, string? body, Exception? transportError)
    {
        if (transportError is not null) return LinkHealth.Unknown;
        if (statusCode is null) return LinkHealth.Unknown;

        var title = HealthBodyHeuristics.ExtractTitle(body) ?? string.Empty;
        if (HealthBodyHeuristics.LooksLikeFilename(title)) return LinkHealth.Alive;

        // 302 redirect → "Buy premium account" is Rapidgator's signature for dead files.
        if (statusCode is 301 or 302) return LinkHealth.Dead;

        if (title.Contains("premium", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("login", StringComparison.OrdinalIgnoreCase))
        {
            return LinkHealth.Dead;
        }

        return LinkHealth.Unknown;
    }

    private static bool Contains(string? s, string needle) =>
        !string.IsNullOrEmpty(s) && s.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
