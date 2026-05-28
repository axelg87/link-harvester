using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace LinkHarvester.Enrichment;

/// <summary>
/// Minimal read-only client for Trakt's public trending lists. Used by
/// DiscoveryRefreshWorker to surface "what people are watching this week."
/// No OAuth flow — the public trending endpoints only need a client_id
/// header (api.trakt.tv treats it as the application identifier).
/// </summary>
public sealed class TraktClient
{
    private const string BaseUrl = "https://api.trakt.tv";

    private readonly HttpClient _http;
    private readonly ILogger<TraktClient> _log;

    public TraktClient(HttpClient http, ILogger<TraktClient> log)
    {
        _http = http;
        _log = log;
        _http.Timeout = TimeSpan.FromSeconds(15);
    }

    public async Task<List<TraktTrendingEntry>> FetchTrendingAsync(string kind, string clientId, int limit, CancellationToken ct)
    {
        // kind ∈ { "movies", "shows" }. Endpoint: /movies/trending or /shows/trending.
        var url = $"{BaseUrl}/{kind}/trending?limit={limit}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("trakt-api-version", "2");
        req.Headers.Add("trakt-api-key", clientId);
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            _log.LogWarning("Trakt {Kind}/trending -> {Code}", kind, (int)resp.StatusCode);
            return new();
        }
        return await resp.Content.ReadFromJsonAsync<List<TraktTrendingEntry>>(cancellationToken: ct) ?? new();
    }
}

public sealed class TraktTrendingEntry
{
    [JsonPropertyName("watchers")] public int Watchers { get; set; }
    [JsonPropertyName("movie")] public TraktItem? Movie { get; set; }
    [JsonPropertyName("show")] public TraktItem? Show { get; set; }
    public TraktItem? Item => Movie ?? Show;
}

public sealed class TraktItem
{
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("year")] public int? Year { get; set; }
    [JsonPropertyName("ids")] public TraktItemIds? Ids { get; set; }
}

public sealed class TraktItemIds
{
    [JsonPropertyName("imdb")] public string? Imdb { get; set; }
    [JsonPropertyName("tmdb")] public int? Tmdb { get; set; }
}
