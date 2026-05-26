using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace LinkHarvester.Synology;

public sealed record QuickConnectResolution(
    string QuickConnectId,
    string BaseUrl,
    IReadOnlyList<string> ProbedUrls,
    DateTimeOffset ResolvedAt);

public interface IQuickConnectResolver
{
    Task<QuickConnectResolution> ResolveAsync(string quickConnectId, CancellationToken ct);
}

public sealed class QuickConnectResolveException : Exception
{
    public QuickConnectResolveException(string message, Exception? inner = null) : base(message, inner) { }
}

public sealed class QuickConnectResolver : IQuickConnectResolver
{
    private static readonly Uri GlobalResolver = new("https://global.quickconnect.to/Serv.php");
    private readonly HttpClient _http;
    private readonly ILogger<QuickConnectResolver> _log;

    public QuickConnectResolver(HttpClient http, ILogger<QuickConnectResolver> log)
    {
        _http = http;
        _log = log;
    }

    public async Task<QuickConnectResolution> ResolveAsync(string quickConnectId, CancellationToken ct)
    {
        var id = quickConnectId.Trim();
        if (string.IsNullOrWhiteSpace(id))
            throw new QuickConnectResolveException("QuickConnect ID is not configured.");

        var endpoints = new Queue<Uri>();
        var seenEndpoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var resolverEndpoints = new List<Uri>();
        endpoints.Enqueue(GlobalResolver);

        var candidates = new List<string>();
        var probeAttempts = new List<string>();

        while (endpoints.Count > 0)
        {
            var endpoint = endpoints.Dequeue();
            if (!seenEndpoints.Add(endpoint.Host)) continue;
            resolverEndpoints.Add(endpoint);

            JsonDocument doc;
            try
            {
                doc = await QueryResolverAsync(endpoint, id, "get_server_info", stopWhenSuccess: false, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                _log.LogWarning(ex, "QuickConnect resolver {Host} failed for ID {QuickConnectId}.", endpoint.Host, id);
                continue;
            }

            using (doc)
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Array) continue;
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    foreach (var site in ReadStringArray(item, "sites"))
                    {
                        if (Uri.TryCreate($"https://{site}/Serv.php", UriKind.Absolute, out var siteUri))
                            endpoints.Enqueue(siteUri);
                    }

                    if (ReadInt(item, "errno") != 0) continue;
                    candidates.AddRange(ReadCandidates(item));
                }
            }

            var distinctCandidates = candidates
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var candidate in distinctCandidates)
            {
                if (probeAttempts.Contains(candidate, StringComparer.OrdinalIgnoreCase)) continue;
                probeAttempts.Add(candidate);
                if (await IsDsmDownloadStationEndpointAsync(candidate, ct))
                {
                    return new QuickConnectResolution(id, candidate, probeAttempts, DateTimeOffset.UtcNow);
                }
            }
        }

        for (var endpointIndex = 0; endpointIndex < resolverEndpoints.Count; endpointIndex++)
        {
            var endpoint = resolverEndpoints[endpointIndex];
            JsonDocument doc;
            try
            {
                doc = await QueryResolverAsync(endpoint, id, "request_tunnel", stopWhenSuccess: true, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                _log.LogWarning(ex, "QuickConnect tunnel request {Host} failed for ID {QuickConnectId}.", endpoint.Host, id);
                continue;
            }

            using (doc)
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Array) continue;
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    foreach (var site in ReadStringArray(item, "sites"))
                    {
                        if (Uri.TryCreate($"https://{site}/Serv.php", UriKind.Absolute, out var siteUri)
                            && resolverEndpoints.All(e => !string.Equals(e.Host, siteUri.Host, StringComparison.OrdinalIgnoreCase)))
                        {
                            resolverEndpoints.Add(siteUri);
                        }
                    }

                    if (ReadInt(item, "errno") != 0) continue;
                    candidates.AddRange(ReadCandidates(item));
                }
            }

            var distinctCandidates = candidates
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var candidate in distinctCandidates)
            {
                if (probeAttempts.Contains(candidate, StringComparer.OrdinalIgnoreCase)) continue;
                probeAttempts.Add(candidate);
                if (await IsDsmDownloadStationEndpointAsync(candidate, ct))
                {
                    return new QuickConnectResolution(id, candidate, probeAttempts, DateTimeOffset.UtcNow);
                }
            }
        }

        var attempted = probeAttempts.Count == 0
            ? "No DSM endpoint candidates were returned by QuickConnect."
            : $"Tried {probeAttempts.Count} QuickConnect endpoint candidate(s), but none returned DSM DownloadStation API metadata.";
        throw new QuickConnectResolveException(attempted);
    }

    private async Task<JsonDocument> QueryResolverAsync(
        Uri endpoint,
        string quickConnectId,
        string command,
        bool stopWhenSuccess,
        CancellationToken ct)
    {
        var payload = new[]
        {
            new QuickConnectRequest(command, "dsm_portal_https", quickConnectId, stop_when_success: stopWhenSuccess),
            new QuickConnectRequest(command, "dsm_portal", quickConnectId, stop_when_success: stopWhenSuccess)
        };
        using var resp = await _http.PostAsJsonAsync(endpoint, payload, ct);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    private async Task<bool> IsDsmDownloadStationEndpointAsync(string baseUrl, CancellationToken ct)
    {
        var endpoint = new Uri(new Uri(baseUrl), "/webapi/entry.cgi?api=SYNO.API.Info&version=1&method=query&query=SYNO.API.Auth,SYNO.DownloadStation2.Task");
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, endpoint);
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return false;

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (!doc.RootElement.TryGetProperty("success", out var success) || !success.GetBoolean())
                return false;
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                return false;
            return data.TryGetProperty("SYNO.API.Auth", out _)
                && data.TryGetProperty("SYNO.DownloadStation2.Task", out _);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            _log.LogDebug(ex, "QuickConnect candidate {BaseUrl} did not probe as DSM.", baseUrl);
            return false;
        }
    }

    private static IEnumerable<string> ReadCandidates(JsonElement item)
    {
        var service = ReadObject(item, "service");
        var smartDns = ReadObject(item, "smartdns");
        var port = ReadInt(service, "port");
        var externalPort = ReadInt(service, "ext_port");
        var relayPort = ReadInt(service, "relay_port");
        var pingPong = ReadString(service, "pingpong");

        var direct = new List<string>();
        var relay = new List<string>();

        AddHostCandidate(direct, ReadString(smartDns, "external"), externalPort > 0 ? externalPort : port);
        AddHostCandidate(direct, ReadString(smartDns, "host"), port);

        AddHostCandidate(relay, ReadString(service, "relay_dn"), relayPort);
        AddHostCandidate(relay, ReadString(service, "relay_dualstack"), relayPort);

        return string.Equals(pingPong, "CONNECTED", StringComparison.OrdinalIgnoreCase)
            ? direct.Concat(relay)
            : relay.Concat(direct);
    }

    private static void AddHostCandidate(List<string> candidates, string? host, int port)
    {
        if (string.IsNullOrWhiteSpace(host) || port <= 0) return;
        candidates.Add($"https://{host}:{port}");
    }

    private static JsonElement ReadObject(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : default;

    private static string? ReadString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(property, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int ReadInt(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(property, out var value)
           && value.TryGetInt32(out var number)
            ? number
            : 0;

    private static IEnumerable<string> ReadStringArray(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } s)
                yield return s;
        }
    }

    private sealed record QuickConnectRequest(
        string command,
        string id,
        string serverID,
        int version = 1,
        bool stop_when_error = false,
        bool stop_when_success = false,
        bool is_gofile = false);
}
