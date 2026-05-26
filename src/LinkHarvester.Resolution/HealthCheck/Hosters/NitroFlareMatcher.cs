namespace LinkHarvester.Resolution.HealthCheck.Hosters;

/// <summary>
/// NitroFlare: HTTP 404 = dead. Otherwise: alive if title contains a filename,
/// dead-ish (generic landing "NitroFlare - Upload Files") on 200 without filename.
/// </summary>
public sealed class NitroFlareMatcher : IHosterHealthMatcher
{
    public bool Matches(string url, string? hosterName) =>
        Contains(hosterName, "nitroflare") || Contains(url, "nitroflare.com");

    public LinkHealth Evaluate(int? statusCode, string? body, Exception? transportError)
    {
        if (transportError is not null) return LinkHealth.Unknown;
        if (statusCode == 404) return LinkHealth.Dead;

        var title = HealthBodyHeuristics.ExtractTitle(body) ?? string.Empty;
        if (HealthBodyHeuristics.LooksLikeFilename(title)) return LinkHealth.Alive;

        // Generic landing page = file gone.
        if (title.Contains("Upload Files", StringComparison.OrdinalIgnoreCase) ||
            title.Equals("NitroFlare", StringComparison.OrdinalIgnoreCase))
        {
            return LinkHealth.Dead;
        }

        if (statusCode is null) return LinkHealth.Unknown;
        return LinkHealth.Alive;
    }

    private static bool Contains(string? s, string needle) =>
        !string.IsNullOrEmpty(s) && s.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
