using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace LinkHarvester.Enrichment;

/// <summary>
/// Minimal client for TMDB v3 (api.themoviedb.org). Stateless; the caller
/// supplies the API key per request, which lets us pick up rotation from
/// SettingsService without restarting the worker.
///
/// Endpoints used:
///   GET /3/movie/{id}
///   GET /3/tv/{id}
///   GET /3/find/{imdb_id}?external_source=imdb_id
/// </summary>
public sealed class TmdbClient
{
    private const string BaseUrl = "https://api.themoviedb.org/3";

    private readonly HttpClient _http;
    private readonly ILogger<TmdbClient> _log;

    public TmdbClient(HttpClient http, ILogger<TmdbClient> log)
    {
        _http = http;
        _log = log;
        _http.Timeout = TimeSpan.FromSeconds(15);
    }

    public async Task<TmdbDetails?> FetchByTmdbIdAsync(int tmdbId, bool isSeries, string apiKey, CancellationToken ct)
    {
        var path = isSeries ? $"/tv/{tmdbId}" : $"/movie/{tmdbId}";
        return await FetchAsync(path, apiKey, isSeries, ct);
    }

    public async Task<TmdbFindResult?> FindByImdbAsync(string imdbId, string apiKey, CancellationToken ct)
    {
        var url = $"{BaseUrl}/find/{Uri.EscapeDataString(imdbId)}?external_source=imdb_id&api_key={Uri.EscapeDataString(apiKey)}";
        using var resp = await _http.GetAsync(url, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        if (!resp.IsSuccessStatusCode)
        {
            _log.LogWarning("TMDB find/{Imdb} -> {Code}", imdbId, (int)resp.StatusCode);
            return null;
        }
        return await resp.Content.ReadFromJsonAsync<TmdbFindResult>(cancellationToken: ct);
    }

    private async Task<TmdbDetails?> FetchAsync(string path, string apiKey, bool isSeries, CancellationToken ct)
    {
        var url = $"{BaseUrl}{path}?api_key={Uri.EscapeDataString(apiKey)}&language=fr-FR";
        using var resp = await _http.GetAsync(url, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        if (resp.StatusCode == (HttpStatusCode)429)
            throw new TmdbRateLimitException(resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2));
        if (!resp.IsSuccessStatusCode)
        {
            _log.LogWarning("TMDB {Path} -> {Code}", path, (int)resp.StatusCode);
            return null;
        }
        var json = await resp.Content.ReadAsStringAsync(ct);
        return ParseDetails(json, isSeries);
    }

    private static TmdbDetails? ParseDetails(string json, bool isSeries)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var d = new TmdbDetails
            {
                TmdbId = root.TryGetProperty("id", out var id) ? id.GetInt32() : 0,
                ImdbId = root.TryGetProperty("imdb_id", out var im) ? im.GetString() : null,
                ReleaseDate = isSeries
                    ? (root.TryGetProperty("first_air_date", out var fa) ? fa.GetString() : null)
                    : (root.TryGetProperty("release_date", out var rd) ? rd.GetString() : null),
                Runtime = isSeries
                    ? (root.TryGetProperty("episode_run_time", out var er) && er.ValueKind == JsonValueKind.Array && er.GetArrayLength() > 0 ? er[0].GetInt32() : (int?)null)
                    : (root.TryGetProperty("runtime", out var r) && r.ValueKind != JsonValueKind.Null ? r.GetInt32() : (int?)null),
                VoteAverage = root.TryGetProperty("vote_average", out var va) ? va.GetDouble() : 0,
                VoteCount = root.TryGetProperty("vote_count", out var vc) ? vc.GetInt32() : 0,
                Popularity = root.TryGetProperty("popularity", out var pp) ? pp.GetDouble() : 0,
                OriginalLanguage = root.TryGetProperty("original_language", out var ol) ? ol.GetString() : null,
                Overview = root.TryGetProperty("overview", out var ov) ? ov.GetString() : null,
                Status = root.TryGetProperty("status", out var st) ? st.GetString() : null,
            };
            if (root.TryGetProperty("genres", out var g) && g.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in g.EnumerateArray())
                {
                    if (item.TryGetProperty("name", out var name)) d.Genres.Add(name.GetString() ?? "");
                }
            }
            if (!string.IsNullOrEmpty(d.ReleaseDate) && d.ReleaseDate!.Length >= 4 && int.TryParse(d.ReleaseDate[..4], out var y))
                d.Year = y;
            return d;
        }
        catch
        {
            return null;
        }
    }
}

public sealed class TmdbDetails
{
    public int TmdbId { get; set; }
    public string? ImdbId { get; set; }
    public string? ReleaseDate { get; set; }
    public int? Year { get; set; }
    public int? Runtime { get; set; }
    public double VoteAverage { get; set; }
    public int VoteCount { get; set; }
    public double Popularity { get; set; }
    public string? OriginalLanguage { get; set; }
    public string? Overview { get; set; }
    public string? Status { get; set; }
    public List<string> Genres { get; set; } = new();
}

public sealed class TmdbFindResult
{
    [JsonPropertyName("movie_results")] public List<TmdbFindEntry>? MovieResults { get; set; }
    [JsonPropertyName("tv_results")] public List<TmdbFindEntry>? TvResults { get; set; }
}

public sealed class TmdbFindEntry
{
    [JsonPropertyName("id")] public int Id { get; set; }
}

public sealed class TmdbRateLimitException : Exception
{
    public TimeSpan RetryAfter { get; }
    public TmdbRateLimitException(TimeSpan retryAfter) : base($"TMDB rate limit, retry after {retryAfter}")
    { RetryAfter = retryAfter; }
}
