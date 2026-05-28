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
}

public sealed record PlexMovie(string Title, int? Year);

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
}
