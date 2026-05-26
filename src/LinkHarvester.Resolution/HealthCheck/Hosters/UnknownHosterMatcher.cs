namespace LinkHarvester.Resolution.HealthCheck.Hosters;

/// <summary>
/// Fallback matcher for hosters we have no explicit strategy for
/// (e.g. Darkibox, Send.cm, Turbobit). Always returns Unknown so the link is
/// never auto-hidden purely because we couldn't probe it.
/// </summary>
public sealed class UnknownHosterMatcher : IHosterHealthMatcher
{
    public bool Matches(string url, string? hosterName) => true;
    public LinkHealth Evaluate(int? statusCode, string? body, Exception? transportError) => LinkHealth.Unknown;
}
