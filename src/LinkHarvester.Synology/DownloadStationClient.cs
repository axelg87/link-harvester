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
///
/// Errors:
///   All failure paths throw <see cref="DsmException"/>; the API/Worker layer
///   surfaces <see cref="DsmException.HumanMessage"/> directly to the UI. We
///   deliberately do not leak raw DSM error codes or response bodies to
///   callers — only the structured exception carries them.
/// </summary>
public sealed class DownloadStationClient : IDownloadStationClient
{
    private readonly HttpClient _http;
    private readonly ISettingsService _settings;
    private readonly IQuickConnectEndpointService _quickConnectEndpoints;
    private readonly ILogger<DownloadStationClient> _log;
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private string? _sid;
    private DateTimeOffset _sidObtainedAt;
    private string? _sidForBaseUrl;

    public DownloadStationClient(
        HttpClient http,
        ISettingsService settings,
        IQuickConnectEndpointService quickConnectEndpoints,
        ILogger<DownloadStationClient> log)
    {
        _http = http;
        _settings = settings;
        _quickConnectEndpoints = quickConnectEndpoints;
        _log = log;
        _http.Timeout = TimeSpan.FromSeconds(30);
        _settings.Changed += () => { _sid = null; }; // force re-auth on settings change
    }

    public async Task<IReadOnlyList<string>> CreateTasksAsync(IEnumerable<string> urls, string? destination, CancellationToken ct)
    {
        try
        {
            return await CreateTasksCoreAsync(urls, destination, forceQuickConnectRefresh: false, ct);
        }
        catch (DsmException ex) when (ShouldRefreshQuickConnectEndpoint(ex))
        {
            _sid = null;
            _log.LogWarning(ex, "Synology request failed against the current QuickConnect endpoint; refreshing once.");
            return await CreateTasksCoreAsync(urls, destination, forceQuickConnectRefresh: true, ct);
        }
    }

    private async Task<IReadOnlyList<string>> CreateTasksCoreAsync(
        IEnumerable<string> urls,
        string? destination,
        bool forceQuickConnectRefresh,
        CancellationToken ct)
    {
        var s = _settings.Current;
        var baseUrl = await _quickConnectEndpoints.EnsureResolvedBaseUrlAsync(forceQuickConnectRefresh, ct);
        s = _settings.Current;
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw DsmException.NotConfigured("Base URL");
        if (string.IsNullOrWhiteSpace(s.SynologyUsername) || string.IsNullOrWhiteSpace(s.SynologyPassword))
            throw DsmException.NotConfigured("credentials");
        var urlList = urls.Where(u => !string.IsNullOrWhiteSpace(u)).Distinct().ToList();

        await EnsureSessionAsync(s, baseUrl, ct);

        // Empty-list call is used by the settings UI as a connection test.
        if (urlList.Count == 0) return Array.Empty<string>();

        var endpoint = new Uri(new Uri(baseUrl), "/webapi/entry.cgi");
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

        HttpResponseMessage resp;
        string body;
        try
        {
            using var content = new FormUrlEncodedContent(form);
            resp = await _http.PostAsync(endpoint, content, ct);
            body = await resp.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw DsmException.ForTransport(baseUrl, ex);
        }

        if (!resp.IsSuccessStatusCode)
        {
            throw DsmException.ForHttp(resp.StatusCode, baseUrl, body);
        }

        DsmEnvelope<DsmTaskListResponse>? parsed;
        try { parsed = JsonSerializer.Deserialize<DsmEnvelope<DsmTaskListResponse>>(body); }
        catch (JsonException) { parsed = null; }

        if (parsed is null)
            throw DsmException.ForUnparseable(baseUrl, body);

        if (!parsed.Success)
        {
            var code = parsed.Error?.Code ?? 0;
            // Reset SID on session-related codes so the next call re-auths.
            if (code is 105 or 119) _sid = null;

            // DSM2 400 includes a per-URL `errors[].url` breakdown when the
            // hoster isn't accepted; surface the first failing URL.
            var firstBadUrl = parsed.Error?.Errors?.FirstOrDefault()?.Url;
            throw DsmException.ForCode(code, s.SynologyUsername, baseUrl, failedUrl: firstBadUrl);
        }

        var ids = parsed.Data?.Task?.Select(t => t.TaskId ?? string.Empty).Where(s => s.Length > 0).ToList()
                  ?? new List<string>();
        _log.LogInformation("DownloadStation accepted {Count} URL(s); task ids: {Ids}",
            urlList.Count, string.Join(",", ids));
        return ids;
    }

    private async Task EnsureSessionAsync(AppSettingsSnapshot s, string baseUrl, CancellationToken ct)
    {
        if (_sid is not null && _sidForBaseUrl == baseUrl
            && DateTimeOffset.UtcNow - _sidObtainedAt < TimeSpan.FromMinutes(30)) return;
        await _sessionGate.WaitAsync(ct);
        try
        {
            if (_sid is not null && _sidForBaseUrl == baseUrl
                && DateTimeOffset.UtcNow - _sidObtainedAt < TimeSpan.FromMinutes(30)) return;

            var endpoint = new Uri(new Uri(baseUrl), "/webapi/entry.cgi");
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

            HttpResponseMessage resp;
            string body;
            try
            {
                using var content = new FormUrlEncodedContent(form);
                resp = await _http.PostAsync(endpoint, content, ct);
                body = await resp.Content.ReadAsStringAsync(ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw DsmException.ForTransport(baseUrl, ex);
            }

            if (!resp.IsSuccessStatusCode)
                throw DsmException.ForHttp(resp.StatusCode, baseUrl, body);

            DsmEnvelope<DsmAuthResponse>? env;
            try { env = JsonSerializer.Deserialize<DsmEnvelope<DsmAuthResponse>>(body); }
            catch (JsonException) { env = null; }

            if (env is null)
                throw DsmException.ForUnparseable(baseUrl, body);

            if (!env.Success || env.Data?.Sid is null)
                throw DsmException.ForCode(env.Error?.Code ?? 0, s.SynologyUsername, baseUrl);

            _sid = env.Data.Sid;
            _sidObtainedAt = DateTimeOffset.UtcNow;
            _sidForBaseUrl = baseUrl;
            _log.LogInformation("Synology login succeeded.");
        }
        finally { _sessionGate.Release(); }
    }

    public async Task EnsureFoldersAsync(IEnumerable<string> folderPaths, CancellationToken ct)
    {
        var paths = folderPaths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim().Trim('/'))
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(p => p.Length) // shallow → deep, so parents are created first
            .ToList();
        if (paths.Count == 0) return;

        var s = _settings.Current;
        var baseUrl = await _quickConnectEndpoints.EnsureResolvedBaseUrlAsync(forceRefresh: false, ct);
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw DsmException.NotConfigured("Base URL");
        if (string.IsNullOrWhiteSpace(s.SynologyUsername) || string.IsNullOrWhiteSpace(s.SynologyPassword))
            throw DsmException.NotConfigured("credentials");

        await EnsureSessionAsync(s, baseUrl, ct);

        var endpoint = new Uri(new Uri(baseUrl), "/webapi/entry.cgi");
        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            var slash = path.LastIndexOf('/');
            var parent = slash > 0 ? path[..slash] : path;
            var name = slash > 0 ? path[(slash + 1)..] : path;
            if (slash <= 0)
            {
                // No nesting (e.g. "video"); skip — only mkdir leaf-with-parent shapes.
                continue;
            }

            var form = new Dictionary<string, string>
            {
                ["api"] = "SYNO.FileStation.CreateFolder",
                ["version"] = "2",
                ["method"] = "create",
                ["folder_path"] = $"\"/{parent}\"",
                ["name"] = $"\"{name}\"",
                ["force_parent"] = "true",
                ["_sid"] = _sid!
            };

            HttpResponseMessage resp;
            string body;
            try
            {
                using var content = new FormUrlEncodedContent(form);
                resp = await _http.PostAsync(endpoint, content, ct);
                body = await resp.Content.ReadAsStringAsync(ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw DsmException.ForTransport(baseUrl, ex);
            }

            if (!resp.IsSuccessStatusCode)
                throw DsmException.ForHttp(resp.StatusCode, baseUrl, body);

            DsmEnvelope<object>? parsed;
            try { parsed = JsonSerializer.Deserialize<DsmEnvelope<object>>(body); }
            catch (JsonException) { parsed = null; }
            if (parsed is null)
                throw DsmException.ForUnparseable(baseUrl, body);

            if (!parsed.Success)
            {
                var code = parsed.Error?.Code ?? 0;
                // Code 408 = file/folder already exists. That's the happy idempotent path.
                if (code == 408) { _log.LogDebug("FileStation: folder /{Path} already exists", path); continue; }
                if (code is 105 or 119) _sid = null;
                throw DsmException.ForCode(code, s.SynologyUsername, baseUrl, failedUrl: null);
            }

            _log.LogInformation("FileStation: created folder /{Path}", path);
        }
    }

    private bool ShouldRefreshQuickConnectEndpoint(DsmException ex)
    {
        if (_settings.Current.SynologyConnectionMode != SynologyConnectionMode.QuickConnect)
            return false;
        // We deliberately only retry on SyntheticUnreachable — DNS / connection
        // refused / network unreachable. Those happen before any bytes hit
        // DSM so a re-resolve + retry is safe.
        //
        // We DO NOT retry on SyntheticTimeout, SyntheticUnparseable, or 5xx
        // gateway codes: those can fire after DSM has already accepted the
        // create-task POST. Retrying then produces a duplicate download —
        // exactly the bug we just fixed.
        return ex.Code is DsmException.SyntheticUnreachable;
    }

    private sealed record DsmEnvelope<T>(
        [property: JsonPropertyName("success")] bool Success,
        [property: JsonPropertyName("data")] T? Data,
        [property: JsonPropertyName("error")] DsmError? Error);

    private sealed record DsmError(
        [property: JsonPropertyName("code")] int Code,
        [property: JsonPropertyName("errors")] List<DsmErrorDetail>? Errors);

    private sealed record DsmErrorDetail(
        [property: JsonPropertyName("url")] string? Url,
        [property: JsonPropertyName("error")] int? Error);

    private sealed record DsmAuthResponse(
        [property: JsonPropertyName("sid")] string? Sid);

    private sealed record DsmTaskListResponse(
        [property: JsonPropertyName("task")] List<DsmTask>? Task);

    private sealed record DsmTask(
        [property: JsonPropertyName("task_id")] string? TaskId);
}
