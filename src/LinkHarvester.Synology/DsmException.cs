using System.Net;
using System.Net.Sockets;

namespace LinkHarvester.Synology;

/// <summary>
/// Thrown by <see cref="DownloadStationClient"/> when DSM rejects a request
/// or is unreachable. Carries both the raw <see cref="Code"/> (DSM API code
/// or a synthetic value for transport faults) and a <see cref="HumanMessage"/>
/// suitable for direct display in the UI.
///
/// Synology DSM codes seen in production:
///   105 — Insufficient permissions (user lacks DownloadStation access).
///   119 — SID not found (session expired).
///   400 — Task creation failed (typically AllDebrid .host plugin missing or
///         the hoster isn't registered with it).
///   403 — Authentication failed at login.
///   404 — DSM endpoint not reachable (wrong BaseUrl path).
///
/// Plus our own synthetic codes for transport-level faults
/// (<see cref="SyntheticConnectionRefused"/> etc.) so the API layer doesn't
/// have to inspect inner exceptions.
/// </summary>
public sealed class DsmException : Exception
{
    /// <summary>Synthetic code: app could not reach the DSM host at all (DNS / refused / closed).</summary>
    public const int SyntheticUnreachable = -1;

    /// <summary>Synthetic code: a request reached DSM but exceeded the configured timeout.</summary>
    public const int SyntheticTimeout = -2;

    /// <summary>Synthetic code: DSM returned a response we couldn't parse.</summary>
    public const int SyntheticUnparseable = -3;

    /// <summary>Synthetic code: required Synology settings (BaseUrl, credentials) aren't configured.</summary>
    public const int SyntheticNotConfigured = -4;

    public int Code { get; }

    /// <summary>UI-safe, end-user-actionable message. Never contains raw response bodies or stack traces.</summary>
    public string HumanMessage { get; }

    /// <summary>The URL DSM rejected, if the error was per-URL (DSM2 400 with errors[].url).</summary>
    public string? FailedUrl { get; }

    public DsmException(int code, string humanMessage, string? failedUrl = null, Exception? inner = null)
        : base(humanMessage, inner)
    {
        Code = code;
        HumanMessage = humanMessage;
        FailedUrl = failedUrl;
    }

    /// <summary>
    /// Builds a <see cref="DsmException"/> with a friendly message for a
    /// DSM-side API code. <paramref name="username"/> and
    /// <paramref name="baseUrl"/> are interpolated into the message when
    /// helpful — pass empty strings if unknown.
    /// </summary>
    public static DsmException ForCode(int code, string username, string baseUrl, string? failedUrl = null, string? rawDetail = null)
    {
        var msg = code switch
        {
            105 => string.IsNullOrEmpty(username)
                ? "The DSM user doesn't have DownloadStation permission. Check DSM → Control Panel → User → Applications."
                : $"The DSM user '{username}' doesn't have DownloadStation permission. Check DSM → Control Panel → User → Applications.",
            119 => "Synology session expired. Please retry — the client will re-authenticate.",
            400 when failedUrl is not null =>
                $"DSM refused the URL: {failedUrl}. Most likely the AllDebrid .host plugin isn't installed or this hoster isn't registered in it.",
            400 => "DSM refused the task. Likely the destination folder doesn't exist or the AllDebrid .host plugin isn't installed.",
            401 => "DSM rejected the request: not authenticated. Check Synology credentials.",
            403 => "DSM rejected the login. Check the username and password under Settings → Synology.",
            404 => string.IsNullOrEmpty(baseUrl)
                ? "DSM endpoint not found. Verify the Base URL under Settings → Synology."
                : $"DSM endpoint not found at '{baseUrl}'. Verify the Base URL under Settings → Synology.",
            407 => "DSM requires 2FA. Add the OTP code under Settings → Synology.",
            408 => "DSM session expired. Please retry — the client will re-authenticate.",
            _ => string.IsNullOrEmpty(rawDetail)
                ? $"Synology rejected the request (code {code})."
                : $"Synology rejected the request (code {code}): {Truncate(rawDetail, 200)}",
        };
        return new DsmException(code, msg, failedUrl);
    }

    /// <summary>Builds a friendly message for a transport-level failure (DNS, refused, timeout).</summary>
    public static DsmException ForTransport(string baseUrl, Exception inner)
    {
        // Walk the inner-exception chain looking for a known signature.
        for (var e = (Exception?)inner; e is not null; e = e.InnerException)
        {
            if (e is TaskCanceledException or TimeoutException)
            {
                var msg = string.IsNullOrEmpty(baseUrl)
                    ? "Timed out talking to DSM. Is the NAS online and reachable?"
                    : $"Timed out talking to DSM at '{baseUrl}'. Is the NAS online and reachable?";
                return new DsmException(SyntheticTimeout, msg, inner: inner);
            }
            if (e is SocketException se)
            {
                var msg = se.SocketErrorCode switch
                {
                    SocketError.HostNotFound or SocketError.NoData =>
                        $"Couldn't resolve DSM hostname '{baseUrl}'. Is the QuickConnect URL correct?",
                    SocketError.ConnectionRefused =>
                        $"DSM refused the connection at '{baseUrl}'. Is DownloadStation enabled and the port open?",
                    SocketError.NetworkUnreachable or SocketError.HostUnreachable =>
                        $"DSM is unreachable at '{baseUrl}'. Check your network or VPN.",
                    _ => $"Couldn't reach DSM at '{baseUrl}' ({se.SocketErrorCode}).",
                };
                return new DsmException(SyntheticUnreachable, msg, inner: inner);
            }
        }
        var fallback = string.IsNullOrEmpty(baseUrl)
            ? "Couldn't reach DSM. Check the Base URL and that the NAS is online."
            : $"Couldn't reach DSM at '{baseUrl}'. Check the Base URL and that the NAS is online.";
        return new DsmException(SyntheticUnreachable, fallback, inner: inner);
    }

    /// <summary>Builds a friendly message for HTTP-level status faults (used when DSM returns non-2xx with no parseable envelope).</summary>
    public static DsmException ForHttp(HttpStatusCode status, string baseUrl, string? body, Exception? inner = null)
    {
        var code = (int)status;
        var msg = status switch
        {
            HttpStatusCode.NotFound =>
                $"DSM endpoint not found at '{baseUrl}'. The web service path is wrong, or DownloadStation isn't installed.",
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                "DSM rejected the request: not authorized. Check the username and password under Settings → Synology.",
            HttpStatusCode.GatewayTimeout or HttpStatusCode.RequestTimeout =>
                $"DSM timed out responding from '{baseUrl}'. Is the NAS overloaded or offline?",
            HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable =>
                $"DSM at '{baseUrl}' is temporarily unavailable. Try again in a moment.",
            _ => $"DSM returned HTTP {code} from '{baseUrl}'.",
        };
        return new DsmException(code, msg, inner: inner);
    }

    /// <summary>Builds a friendly message for an unparseable DSM response body.</summary>
    public static DsmException ForUnparseable(string baseUrl, string? body, Exception? inner = null)
    {
        var msg = $"DSM at '{baseUrl}' returned an unexpected response. The Base URL may point at a non-DSM server, or DSM is in maintenance mode.";
        return new DsmException(SyntheticUnparseable, msg, inner: inner);
    }

    /// <summary>Synology settings missing — distinct from a real DSM rejection so callers can guide the user to Settings.</summary>
    public static DsmException NotConfigured(string what)
        => new(SyntheticNotConfigured, $"Synology {what} isn't set. Go to Settings → Synology to configure it.");

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";
}
