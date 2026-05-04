using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using LinkHarvester.Core;
using Microsoft.Extensions.Logging;

namespace LinkHarvester.Synology;

/// <summary>
/// Synology DSM 7 DownloadStation client.
///
/// Auth: SYNO.API.Auth v6 (login, returns sid).
/// Submit: SYNO.DownloadStation2.Task v2 (POST with `url` array as JSON-encoded list).
/// </summary>
public sealed class DownloadStationClient : IDownloadStationClient
{
    private readonly HttpClient _http;
    private readonly ISettingsService _settings;
    private readonly ILogger<DownloadStationClient> _log;
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private string? _sid;
    private DateTimeOffset _sidObtainedAt;
    private string? _sidForBaseUrl;

    public DownloadStationClient(HttpClient http, ISettingsService settings, ILogger<DownloadStationClient> log)
    {
        _http = http;
        _settings = settings;
        _log = log;
        _http.Timeout = TimeSpan.FromSeconds(30);
        _settings.Changed += () => { _sid = null; }; // force re-auth on settings change
    }

    public async Task<IReadOnlyList<string>> CreateTasksAsync(IEnumerable<string> urls, string? destination, CancellationToken ct)
    {
        var s = _settings.Current;
        if (string.IsNullOrWhiteSpace(s.SynologyBaseUrl))
            throw new InvalidOperationException("Synology BaseUrl is not configured.");
        if (string.IsNullOrWhiteSpace(s.SynologyUsername) || string.IsNullOrWhiteSpace(s.SynologyPassword))
            throw new InvalidOperationException("Synology credentials are not configured.");
        var urlList = urls.Where(u => !string.IsNullOrWhiteSpace(u)).Distinct().ToList();

        await EnsureSessionAsync(ct);

        // Empty-list call is used by the settings UI as a connection test.
        if (urlList.Count == 0) return Array.Empty<string>();

        var endpoint = new Uri(new Uri(s.SynologyBaseUrl), "/webapi/entry.cgi");
        var form = new Dictionary<string, string>
        {
            ["api"] = "SYNO.DownloadStation2.Task",
            ["version"] = "2",
            ["method"] = "create",
            ["type"] = "\"url\"",
            ["create_list"] = "false",
            ["url"] = JsonSerializer.Serialize(urlList),
            ["_sid"] = _sid!
        };
        if (!string.IsNullOrEmpty(destination))
        {
            form["destination"] = $"\"{destination}\"";
        }

        using var content = new FormUrlEncodedContent(form);
        using var resp = await _http.PostAsync(endpoint, content, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        var parsed = JsonSerializer.Deserialize<DsmEnvelope<DsmTaskListResponse>>(body)
            ?? throw new InvalidOperationException($"Unparseable Synology response: {body}");
        if (!parsed.Success)
        {
            // 105 = Insufficient permissions; 119 = SID not found (session expired).
            if (parsed.Error?.Code is 105 or 119)
            {
                _sid = null;
                throw new InvalidOperationException($"Synology auth error code {parsed.Error?.Code}; session reset");
            }
            throw new InvalidOperationException($"Synology API error: code={parsed.Error?.Code}");
        }

        var ids = parsed.Data?.Task?.Select(t => t.TaskId ?? string.Empty).Where(s => s.Length > 0).ToList()
                  ?? new List<string>();
        _log.LogInformation("DownloadStation accepted {Count} URL(s); task ids: {Ids}",
            urlList.Count, string.Join(",", ids));
        return ids;
    }

    private async Task EnsureSessionAsync(CancellationToken ct)
    {
        var s = _settings.Current;
        if (_sid is not null && _sidForBaseUrl == s.SynologyBaseUrl
            && DateTimeOffset.UtcNow - _sidObtainedAt < TimeSpan.FromMinutes(30)) return;
        await _sessionGate.WaitAsync(ct);
        try
        {
            if (_sid is not null && _sidForBaseUrl == s.SynologyBaseUrl
                && DateTimeOffset.UtcNow - _sidObtainedAt < TimeSpan.FromMinutes(30)) return;

            var endpoint = new Uri(new Uri(s.SynologyBaseUrl), "/webapi/entry.cgi");
            var form = new Dictionary<string, string>
            {
                ["api"] = "SYNO.API.Auth",
                ["version"] = "6",
                ["method"] = "login",
                ["account"] = s.SynologyUsername,
                ["passwd"] = s.SynologyPassword,
                ["session"] = "DownloadStation",
                ["format"] = "sid"
            };
            if (!string.IsNullOrWhiteSpace(s.SynologyOtpCode))
                form["otp_code"] = s.SynologyOtpCode!;

            using var content = new FormUrlEncodedContent(form);
            using var resp = await _http.PostAsync(endpoint, content, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            var env = JsonSerializer.Deserialize<DsmEnvelope<DsmAuthResponse>>(body)
                      ?? throw new InvalidOperationException("Unparseable Synology auth response");
            if (!env.Success || env.Data?.Sid is null)
                throw new InvalidOperationException($"Synology login failed (code={env.Error?.Code})");

            _sid = env.Data.Sid;
            _sidObtainedAt = DateTimeOffset.UtcNow;
            _sidForBaseUrl = s.SynologyBaseUrl;
            _log.LogInformation("Synology login succeeded.");
        }
        finally { _sessionGate.Release(); }
    }

    private sealed record DsmEnvelope<T>(
        [property: JsonPropertyName("success")] bool Success,
        [property: JsonPropertyName("data")] T? Data,
        [property: JsonPropertyName("error")] DsmError? Error);

    private sealed record DsmError(
        [property: JsonPropertyName("code")] int Code);

    private sealed record DsmAuthResponse(
        [property: JsonPropertyName("sid")] string? Sid);

    private sealed record DsmTaskListResponse(
        [property: JsonPropertyName("task")] List<DsmTask>? Task);

    private sealed record DsmTask(
        [property: JsonPropertyName("task_id")] string? TaskId);
}
