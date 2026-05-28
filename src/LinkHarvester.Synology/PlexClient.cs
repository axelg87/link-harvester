using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using LinkHarvester.Core;
using Microsoft.Extensions.Logging;

namespace LinkHarvester.Synology;

/// <summary>
/// Read-only client for the Plex Media Server library API. Only used to
/// enumerate movies the user already owns so the catalog UI can mark them
/// as "on disk". We deliberately don't write to Plex — playback, library
/// scans, etc. stay user-controlled.
///
/// Auth: <c>X-Plex-Token</c> query parameter (the user pulls it from the
/// Plex web UI; see <see href="https://support.plex.tv/articles/204059436-finding-an-authentication-token-x-plex-token"/>).
/// </summary>
public interface IPlexClient
{
    /// <summary>
    /// Returns every movie Plex sees across every movie library section,
    /// or an empty list if Plex isn't configured / unreachable. Never
    /// throws — failures surface via the logger and an empty result so
    /// the catalog falls back to DSM filename matching.
    /// </summary>
    Task<IReadOnlyList<PlexMovie>> ListMoviesAsync(CancellationToken ct);

    /// <summary>True when both BaseUrl and Token are set in settings.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Best-effort fetch from Plex Discover's public watchlist trending feed.
    /// Many tokens lack the required scope and Plex returns 401 / empty;
    /// callers must tolerate an empty result without surfacing it as an error.
    /// </summary>
    Task<IReadOnlyList<PlexDiscoverEntry>> FetchDiscoverWatchlistAsync(string token, CancellationToken ct);
}

public sealed record PlexMovie(string Title, int? Year);

/// <summary>
/// One result from Plex Discover (the public watchlist trending endpoint).
/// Used by DiscoveryRefreshService to surface "most watchlisted on Plex."
/// </summary>
public sealed record PlexDiscoverEntry(string Title, int? Year, int? TmdbId, string? ImdbId);

public sealed class PlexClient : IPlexClient
{
    private readonly HttpClient _http;
    private readonly ISettingsService _settings;
    private readonly ILogger<PlexClient> _log;

    public PlexClient(HttpClient http, ISettingsService settings, ILogger<PlexClient> log)
    {
        _http = http;
        _settings = settings;
        _log = log;
        _http.Timeout = TimeSpan.FromSeconds(20);
    }

    public bool IsConfigured
    {
        get
        {
            var s = _settings.Current;
            return !string.IsNullOrWhiteSpace(s.PlexBaseUrl) && !string.IsNullOrWhiteSpace(s.PlexToken);
        }
    }

    public async Task<IReadOnlyList<PlexMovie>> ListMoviesAsync(CancellationToken ct)
    {
        var s = _settings.Current;
        if (string.IsNullOrWhiteSpace(s.PlexBaseUrl) || string.IsNullOrWhiteSpace(s.PlexToken))
            return Array.Empty<PlexMovie>();

        try
        {
            var sections = await GetAsync<SectionsResponse>(s, "/library/sections", ct);
            if (sections?.MediaContainer?.Directory is null) return Array.Empty<PlexMovie>();

            var movieKeys = sections.MediaContainer.Directory
                .Where(d => string.Equals(d.Type, "movie", StringComparison.OrdinalIgnoreCase))
                .Select(d => d.Key)
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .Distinct()
                .ToList();
            if (movieKeys.Count == 0) return Array.Empty<PlexMovie>();

            var movies = new List<PlexMovie>(capacity: 1024);
            foreach (var key in movieKeys)
            {
                var body = await GetAsync<SectionItemsResponse>(s, $"/library/sections/{key}/all?type=1", ct);
                if (body?.MediaContainer?.Metadata is null) continue;
                foreach (var m in body.MediaContainer.Metadata)
                {
                    if (!string.Equals(m.Type, "movie", StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.IsNullOrWhiteSpace(m.Title)) continue;
                    movies.Add(new PlexMovie(m.Title!, m.Year));
                }
            }
            _log.LogInformation("Plex: enumerated {Count} movie(s) across {Sections} section(s).", movies.Count, movieKeys.Count);
            return movies;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Plex enumerate failed; the catalog will fall back to DSM filename matching.");
            return Array.Empty<PlexMovie>();
        }
    }

    /// <summary>
    /// Best-effort fetch from Plex Discover's public watchlist trending feed.
    /// Many tokens won't have the right scope and Plex returns 401 / empty;
    /// callers must tolerate an empty result without surfacing it as an error.
    /// </summary>
    public async Task<IReadOnlyList<PlexDiscoverEntry>> FetchDiscoverWatchlistAsync(string token, CancellationToken ct)
    {
        // discover.provider.plex.tv exposes the cross-account watchlist
        // trending feed. Endpoint name has shifted over the years; the
        // /library/sections/watchlist/all path is the most stable one and
        // returns JSON when Accept is set to application/json.
        const string url = "https://discover.provider.plex.tv/library/sections/watchlist/all?type=1&sort=playCount:desc&limit=60";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("X-Plex-Token", token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return Array.Empty<PlexDiscoverEntry>();

        var body = await resp.Content.ReadFromJsonAsync<DiscoverResponse>(cancellationToken: ct);
        if (body?.MediaContainer?.Metadata is null) return Array.Empty<PlexDiscoverEntry>();

        var hits = new List<PlexDiscoverEntry>();
        foreach (var m in body.MediaContainer.Metadata)
        {
            if (string.IsNullOrWhiteSpace(m.Title)) continue;
            int? tmdbId = null;
            string? imdbId = null;
            if (m.Guid is { Count: > 0 } guids)
            {
                foreach (var g in guids)
                {
                    if (g.Id is not { } id) continue;
                    if (id.StartsWith("tmdb://", StringComparison.OrdinalIgnoreCase)
                        && int.TryParse(id["tmdb://".Length..], out var t)) tmdbId = t;
                    else if (id.StartsWith("imdb://", StringComparison.OrdinalIgnoreCase)) imdbId = id["imdb://".Length..];
                }
            }
            hits.Add(new PlexDiscoverEntry(m.Title!, m.Year, tmdbId, imdbId));
        }
        return hits;
    }

    private async Task<T?> GetAsync<T>(AppSettingsSnapshot s, string path, CancellationToken ct)
    {
        var url = new Uri(new Uri(s.PlexBaseUrl.TrimEnd('/') + "/"), path.TrimStart('/'));
        // Append token; preserve any existing query string.
        var sep = url.Query.Length == 0 ? "?" : "&";
        var full = new Uri(url + sep + "X-Plex-Token=" + Uri.EscapeDataString(s.PlexToken));

        using var req = new HttpRequestMessage(HttpMethod.Get, full);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Plex HTTP {(int)resp.StatusCode} on {path}");
        return await resp.Content.ReadFromJsonAsync<T>(cancellationToken: ct);
    }

    private sealed record SectionsResponse([property: JsonPropertyName("MediaContainer")] SectionsContainer? MediaContainer);
    private sealed record SectionsContainer([property: JsonPropertyName("Directory")] List<SectionDirectory>? Directory);
    private sealed record SectionDirectory(
        [property: JsonPropertyName("key")] string? Key,
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("title")] string? Title);

    private sealed record SectionItemsResponse([property: JsonPropertyName("MediaContainer")] ItemsContainer? MediaContainer);
    private sealed record ItemsContainer([property: JsonPropertyName("Metadata")] List<ItemMetadata>? Metadata);
    private sealed record ItemMetadata(
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("year")] int? Year);

    private sealed record DiscoverResponse([property: JsonPropertyName("MediaContainer")] DiscoverContainer? MediaContainer);
    private sealed record DiscoverContainer([property: JsonPropertyName("Metadata")] List<DiscoverMetadata>? Metadata);
    private sealed record DiscoverMetadata(
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("year")] int? Year,
        [property: JsonPropertyName("Guid")] List<DiscoverGuid>? Guid);
    private sealed record DiscoverGuid([property: JsonPropertyName("id")] string? Id);
}
