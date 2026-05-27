using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using LinkHarvester.Core;
using Microsoft.Extensions.Logging;

namespace LinkHarvester.Synology;

public interface IFileStationBenchmarkClient
{
    Task<FileStationBenchmarkResult> RunAsync(string folderPath, int iterations, CancellationToken ct);
}

public sealed record FileStationBenchmarkResult(
    bool Ok,
    string? Error,
    string? BaseUrl,
    string? ResolvedPath,
    int FileCount,
    int DirCount,
    long ResponseBytes,
    long LoginMs,
    long ListColdMs,
    long ListWarmP50Ms,
    long ListWarmP95Ms,
    long ListWarmMaxMs,
    long ListWarmMinMs,
    long ListWarmAvgMs,
    int WarmIterations,
    IReadOnlyList<long> WarmTimingsMs);

public sealed class FileStationBenchmarkClient : IFileStationBenchmarkClient
{
    private readonly HttpClient _http;
    private readonly ISettingsService _settings;
    private readonly IQuickConnectEndpointService _quickConnect;
    private readonly ILogger<FileStationBenchmarkClient> _log;

    public FileStationBenchmarkClient(
        HttpClient http,
        ISettingsService settings,
        IQuickConnectEndpointService quickConnect,
        ILogger<FileStationBenchmarkClient> log)
    {
        _http = http;
        _settings = settings;
        _quickConnect = quickConnect;
        _log = log;
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    public async Task<FileStationBenchmarkResult> RunAsync(string folderPath, int iterations, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
            return Empty("folderPath required");

        iterations = Math.Clamp(iterations, 1, 25);

        var s = _settings.Current;
        string baseUrl;
        try
        {
            baseUrl = await _quickConnect.EnsureResolvedBaseUrlAsync(forceRefresh: false, ct);
        }
        catch (Exception ex)
        {
            return Empty($"QuickConnect resolve failed: {ex.Message}");
        }
        s = _settings.Current;
        if (string.IsNullOrWhiteSpace(baseUrl))
            return Empty("Base URL not configured");
        if (string.IsNullOrWhiteSpace(s.SynologyUsername) || string.IsNullOrWhiteSpace(s.SynologyPassword))
            return Empty("Synology credentials missing");

        var endpoint = new Uri(new Uri(baseUrl), "/webapi/entry.cgi");

        // 1) Fresh login (session=FileStation) — measures cold handshake cost.
        var loginSw = Stopwatch.StartNew();
        string sid;
        try
        {
            sid = await LoginAsync(endpoint, s, ct);
        }
        catch (Exception ex)
        {
            return Empty($"Login failed: {ex.Message}");
        }
        loginSw.Stop();

        var normalizedPath = "/" + folderPath.Trim().Trim('/');

        // 2) Cold list — first call on the freshly-authed session.
        long coldMs;
        long bytes;
        int fileCount;
        int dirCount;
        try
        {
            var cold = await ListOnceAsync(endpoint, sid, normalizedPath, ct);
            coldMs = cold.elapsedMs;
            bytes = cold.bytes;
            fileCount = cold.fileCount;
            dirCount = cold.dirCount;
        }
        catch (Exception ex)
        {
            return Empty($"Cold list failed: {ex.Message}") with { LoginMs = loginSw.ElapsedMilliseconds };
        }

        // 3) Warm iterations — repeated List on the same session.
        var warm = new List<long>(iterations);
        for (var i = 0; i < iterations; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var w = await ListOnceAsync(endpoint, sid, normalizedPath, ct);
                warm.Add(w.elapsedMs);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Warm iteration {Iter} failed", i);
            }
        }

        var sorted = warm.OrderBy(x => x).ToList();
        long p(double q) => sorted.Count == 0 ? 0 : sorted[(int)Math.Clamp(Math.Ceiling(q * sorted.Count) - 1, 0, sorted.Count - 1)];

        return new FileStationBenchmarkResult(
            Ok: true,
            Error: null,
            BaseUrl: baseUrl,
            ResolvedPath: normalizedPath,
            FileCount: fileCount,
            DirCount: dirCount,
            ResponseBytes: bytes,
            LoginMs: loginSw.ElapsedMilliseconds,
            ListColdMs: coldMs,
            ListWarmP50Ms: p(0.50),
            ListWarmP95Ms: p(0.95),
            ListWarmMaxMs: sorted.Count > 0 ? sorted[^1] : 0,
            ListWarmMinMs: sorted.Count > 0 ? sorted[0] : 0,
            ListWarmAvgMs: warm.Count > 0 ? (long)warm.Average() : 0,
            WarmIterations: warm.Count,
            WarmTimingsMs: warm);
    }

    private async Task<string> LoginAsync(Uri endpoint, AppSettingsSnapshot s, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["api"] = "SYNO.API.Auth",
            ["version"] = "6",
            ["method"] = "login",
            ["account"] = s.SynologyUsername,
            ["passwd"] = s.SynologyPassword,
            ["session"] = "FileStation",
            ["format"] = "sid"
        };
        if (!string.IsNullOrWhiteSpace(s.SynologyOtpCode))
            form["otp_code"] = s.SynologyOtpCode!;

        using var content = new FormUrlEncodedContent(form);
        using var resp = await _http.PostAsync(endpoint, content, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTP {(int)resp.StatusCode}: {body}");

        var env = JsonSerializer.Deserialize<Envelope<AuthData>>(body)
            ?? throw new InvalidOperationException("Unparseable auth response");
        if (!env.Success || env.Data?.Sid is null)
            throw new InvalidOperationException($"DSM error code={env.Error?.Code ?? 0}");
        return env.Data.Sid;
    }

    private async Task<(long elapsedMs, long bytes, int fileCount, int dirCount)> ListOnceAsync(
        Uri endpoint, string sid, string folderPath, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["api"] = "SYNO.FileStation.List",
            ["version"] = "2",
            ["method"] = "list",
            ["folder_path"] = $"\"{folderPath}\"",
            ["additional"] = "[\"size\",\"time\",\"type\"]",
            ["_sid"] = sid
        };

        var sw = Stopwatch.StartNew();
        using var content = new FormUrlEncodedContent(form);
        using var resp = await _http.PostAsync(endpoint, content, ct);
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        sw.Stop();

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTP {(int)resp.StatusCode}");

        var env = JsonSerializer.Deserialize<Envelope<ListData>>(bytes)
            ?? throw new InvalidOperationException("Unparseable list response");
        if (!env.Success)
            throw new InvalidOperationException($"DSM error code={env.Error?.Code ?? 0}");

        var files = env.Data?.Files ?? new List<FileEntry>();
        var dirCount = files.Count(f => f.IsDir);
        var fileCount = files.Count - dirCount;
        return (sw.ElapsedMilliseconds, bytes.LongLength, fileCount, dirCount);
    }

    private static FileStationBenchmarkResult Empty(string error) => new(
        Ok: false, Error: error, BaseUrl: null, ResolvedPath: null,
        FileCount: 0, DirCount: 0, ResponseBytes: 0,
        LoginMs: 0, ListColdMs: 0,
        ListWarmP50Ms: 0, ListWarmP95Ms: 0, ListWarmMaxMs: 0, ListWarmMinMs: 0, ListWarmAvgMs: 0,
        WarmIterations: 0, WarmTimingsMs: Array.Empty<long>());

    private sealed record Envelope<T>(
        [property: JsonPropertyName("success")] bool Success,
        [property: JsonPropertyName("data")] T? Data,
        [property: JsonPropertyName("error")] Err? Error);
    private sealed record Err([property: JsonPropertyName("code")] int Code);
    private sealed record AuthData([property: JsonPropertyName("sid")] string? Sid);
    private sealed record ListData([property: JsonPropertyName("files")] List<FileEntry>? Files);
    private sealed record FileEntry(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("isdir")] bool IsDir);
}
