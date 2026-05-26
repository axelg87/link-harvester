namespace LinkHarvester.Resolution.HealthCheck;

/// <summary>
/// Per-host strategy for deciding whether a probe response indicates the file
/// is alive, dead, or in an indeterminate state.
///
/// Rule of thumb (the universal negative-signal model):
///   - Return <see cref="LinkHealth.Dead"/> only when there is an explicit
///     dead marker (e.g. clean 404 + body phrase "file does not exist").
///   - Return <see cref="LinkHealth.Alive"/> when there is an explicit alive
///     marker (e.g. file metadata, filename in &lt;title&gt;).
///   - Return <see cref="LinkHealth.Unknown"/> when the response is
///     anti-bot/blocked/transport-error — the file might exist; never auto-hide.
/// </summary>
public interface IHosterHealthMatcher
{
    bool Matches(string url, string? hosterName);
    LinkHealth Evaluate(int? statusCode, string? body, Exception? transportError);
}
