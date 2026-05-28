using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace LinkHarvester.Enrichment;

/// <summary>
/// Polite scraper for letterboxd.com/films/popular/this/week/. No public
/// API — Letterboxd renders HTML with <see langword="film-slug"/> metadata
/// that includes a TMDB id when known. We harvest the first page (~72
/// titles) and resolve to catalog via TMDB id when present, else by
/// (title, year). Rate-limited to one request per refresh cycle; results
/// cached in DiscoveryEntryEntity so the page itself never hits Letterboxd.
/// </summary>
public sealed class LetterboxdPopularScraper
{
    private const string Url = "https://letterboxd.com/films/popular/this/week/";

    // Each film tile looks like:
    //   <li class="poster-container">
    //     <div class="poster ... data-film-slug="se7en" data-film-id="..." ...>
    //       <img alt="Se7en"/>
    //     </div>
    //   </li>
    // The TMDB id isn't always inlined — for popular films it usually is via
    // data-tmdb-id; for less popular ones we fall back to slug + alt-text.
    private static readonly Regex FilmRegex = new(
        "<div[^>]*?class=\"[^\"]*?poster[^\"]*?\"[^>]*?data-film-slug=\"(?<slug>[^\"]+)\"[^>]*?(?:data-film-name=\"(?<name>[^\"]*?)\"[^>]*?)?(?:data-film-release-year=\"(?<year>\\d{4})\"[^>]*?)?(?:data-tmdb-id=\"(?<tmdb>\\d+)\"[^>]*?)?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly HttpClient _http;
    private readonly ILogger<LetterboxdPopularScraper> _log;

    public LetterboxdPopularScraper(HttpClient http, ILogger<LetterboxdPopularScraper> log)
    {
        _http = http;
        _log = log;
        _http.Timeout = TimeSpan.FromSeconds(15);
    }

    public async Task<List<LetterboxdPopularEntry>> FetchAsync(int limit, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(Url, ct);
        if (!resp.IsSuccessStatusCode)
        {
            _log.LogWarning("Letterboxd popular -> {Code}", (int)resp.StatusCode);
            return new();
        }
        var html = await resp.Content.ReadAsStringAsync(ct);
        var hits = new List<LetterboxdPopularEntry>();
        var rank = 0;
        foreach (Match m in FilmRegex.Matches(html))
        {
            rank++;
            if (rank > limit) break;
            int? tmdbId = int.TryParse(m.Groups["tmdb"].Value, out var t) ? t : null;
            int? year = int.TryParse(m.Groups["year"].Value, out var y) ? y : null;
            var slug = m.Groups["slug"].Value;
            var name = m.Groups["name"].Value;
            if (string.IsNullOrEmpty(name))
            {
                // Slug → title fallback: "se7en" → "Se7en". Keep it simple; we
                // primarily resolve via tmdbId when present.
                name = slug.Replace('-', ' ');
                if (name.Length > 0) name = char.ToUpperInvariant(name[0]) + name[1..];
            }
            hits.Add(new LetterboxdPopularEntry(rank, name, year, tmdbId));
        }
        return hits;
    }
}

public sealed record LetterboxdPopularEntry(int Rank, string Title, int? Year, int? TmdbId);
