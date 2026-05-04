using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using LinkHarvester.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
    private readonly SynologyOptions _opts;
    private readonly ILogger<DownloadStationClient> _log;
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private string? _sid;
    private DateTimeOffset _sidObtainedAt;

    public DownloadStationClient(HttpClient http, IOptions<SynologyOptions> opts, ILogger<DownloadStationClient> log)
    {
        _http = http;
        _opts = opts.Value;
        _log = log;
        _http.Timeout = TimeSpan.FromSeconds(_opts.RequestTimeoutSeconds);
    }

    public async Task<IReadOnlyList<string>> CreateTasksAsync(IEnumerable<string> urls, string? destination, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_opts.BaseUrl))
            throw new InvalidOperationException("Synology BaseUrl is not configured.");
        var urlList = urls.Where(u => !string.IsNullOrWhiteSpace(u)).Distinct().ToList();
        if (urlList.Count == 0) return Array.Empty<string>();

        await EnsureSessionAsync(ct);

        var endpoint = new Uri(new Uri(_opts.BaseUrl), "/webapi/entry.cgi");
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
        if (_sid is not null && DateTimeOffset.UtcNow - _sidObtainedAt < TimeSpan.FromMinutes(30)) return;
        await _sessionGate.WaitAsync(ct);
        try
        {
            if (_sid is not null && DateTimeOffset.UtcNow - _sidObtainedAt < TimeSpan.FromMinutes(30)) return;

            var endpoint = new Uri(new Uri(_opts.BaseUrl), "/webapi/entry.cgi");
            var form = new Dictionary<string, string>
            {
                ["api"] = "SYNO.API.Auth",
                ["version"] = "6",
                ["method"] = "login",
                ["account"] = _opts.Username,
                ["passwd"] = _opts.Password,
                ["session"] = "DownloadStation",
                ["format"] = "sid"
            };
            if (!string.IsNullOrWhiteSpace(_opts.OtpCode))
                form["otp_code"] = _opts.OtpCode!;

            using var content = new FormUrlEncodedContent(form);
            using var resp = await _http.PostAsync(endpoint, content, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            var env = JsonSerializer.Deserialize<DsmEnvelope<DsmAuthResponse>>(body)
                      ?? throw new InvalidOperationException("Unparseable Synology auth response");
            if (!env.Success || env.Data?.Sid is null)
                throw new InvalidOperationException($"Synology login failed (code={env.Error?.Code})");

            _sid = env.Data.Sid;
            _sidObtainedAt = DateTimeOffset.UtcNow;
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
