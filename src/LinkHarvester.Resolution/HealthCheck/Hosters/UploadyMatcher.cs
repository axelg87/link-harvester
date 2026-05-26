namespace LinkHarvester.Resolution.HealthCheck.Hosters;

/// <summary>
/// Uploady: HTTP 404 = dead, HTTP 200 with filename in &lt;title&gt; = alive.
/// Title shape: "Download &lt;filename&gt; | Uploady.io" (alive) vs "Download | Uploady.io" (dead).
/// </summary>
public sealed class UploadyMatcher : IHosterHealthMatcher
{
    public bool Matches(string url, string? hosterName) =>
        Contains(hosterName, "uploady") || Contains(url, "uploady.io");

    public LinkHealth Evaluate(int? statusCode, string? body, Exception? transportError)
    {
        if (statusCode == 404) return LinkHealth.Dead;
        if (transportError is not null) return LinkHealth.Unknown;
        if (statusCode == 200 && HasFilenameInTitle(body)) return LinkHealth.Alive;
        if (statusCode is null) return LinkHealth.Unknown;
        return LinkHealth.Alive;
    }

    private static bool Contains(string? s, string needle) =>
        !string.IsNullOrEmpty(s) && s.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static bool HasFilenameInTitle(string? body) =>
        !string.IsNullOrEmpty(body) && HealthBodyHeuristics.HasFilenameInTitle(body);
}
