namespace LinkHarvester.Resolution.HealthCheck.Hosters;

/// <summary>
/// 1Fichier: live files RST the TCP connection (anti-bot) — we observe a transport
/// error or HTTP 000. Dead files return a clean HTTP 404 and a body containing
/// "file does not exist".
/// </summary>
public sealed class OneFichierMatcher : IHosterHealthMatcher
{
    public bool Matches(string url, string? hosterName) =>
        ContainsCi(hosterName, "1fichier") || ContainsCi(url, "1fichier.com");

    public LinkHealth Evaluate(int? statusCode, string? body, Exception? transportError)
    {
        if (statusCode == 404 && body is { Length: > 0 } &&
            body.Contains("file does not exist", StringComparison.OrdinalIgnoreCase))
        {
            return LinkHealth.Dead;
        }

        // Anti-bot RST (transport error) on a known 1fichier URL is the *alive* signature.
        if (transportError is not null) return LinkHealth.Alive;

        // Any other non-404 response (200/302/etc.) without the dead phrase: treat as alive.
        if (statusCode is not null && statusCode != 404) return LinkHealth.Alive;

        return LinkHealth.Unknown;
    }

    private static bool ContainsCi(string? s, string needle) =>
        !string.IsNullOrEmpty(s) && s.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
