namespace LinkHarvester.Resolution.HealthCheck.Hosters;

/// <summary>
/// DailyUploads: alive responses have a filename in the title
/// ("Téléchargement &lt;filename&gt;"); dead responses have a bare title
/// "Téléchargement " (trailing space, no filename).
/// </summary>
public sealed class DailyUploadsMatcher : IHosterHealthMatcher
{
    public bool Matches(string url, string? hosterName) =>
        Contains(hosterName, "dailyuploads") || Contains(url, "dailyuploads.net");

    public LinkHealth Evaluate(int? statusCode, string? body, Exception? transportError)
    {
        if (transportError is not null) return LinkHealth.Unknown;
        if (statusCode is null) return LinkHealth.Unknown;

        var title = HealthBodyHeuristics.ExtractTitle(body) ?? string.Empty;
        if (HealthBodyHeuristics.LooksLikeFilename(title)) return LinkHealth.Alive;

        if (title.Trim().Equals("Téléchargement", StringComparison.OrdinalIgnoreCase) ||
            title.Trim().Equals("Telechargement", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(title))
        {
            return LinkHealth.Dead;
        }

        return LinkHealth.Unknown;
    }

    private static bool Contains(string? s, string needle) =>
        !string.IsNullOrEmpty(s) && s.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
