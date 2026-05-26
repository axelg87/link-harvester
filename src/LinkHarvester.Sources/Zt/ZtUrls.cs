using System.Text.RegularExpressions;
using System.Web;

namespace LinkHarvester.Sources.Zt;

/// <summary>
/// Pure helpers for ZT URLs — no IO. Tested in isolation.
/// </summary>
public static class ZtUrls
{
    public const string BaseUrl = "https://www.zone-telechargement.news";

    private static readonly Regex ArticleHrefRegex =
        new(@"\?p=(?<kind>film|serie|manga|jeu|musique|ebook)&(?:amp;)?id=(?<id>\d+)-(?<slug>[^""''&\s]+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Resolve a possibly-relative href into an absolute ZT URL.
    /// </summary>
    public static string Absolute(string href)
    {
        if (string.IsNullOrWhiteSpace(href)) return string.Empty;
        if (href.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return href;
        if (href.StartsWith("//")) return "https:" + href;
        if (href.StartsWith("/")) return BaseUrl + href;
        if (href.StartsWith("?")) return BaseUrl + "/" + href;
        return BaseUrl + "/" + href;
    }

    /// <summary>
    /// Parse a ZT article URL into (kind-string, id, slug). Null if not recognised.
    /// </summary>
    public static (string Kind, string Id, string Slug)? ParseArticle(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var decoded = HttpUtility.HtmlDecode(url);
        var m = ArticleHrefRegex.Match(decoded);
        if (!m.Success) return null;
        return (m.Groups["kind"].Value.ToLowerInvariant(),
                m.Groups["id"].Value,
                m.Groups["slug"].Value);
    }

    public static string ExternalId(string kind, string id) => $"{kind}:{id}";

    /// <summary>
    /// Category-listing URL for a given kind and page index (1-based).
    /// Maps backfill kinds to ZT URL slugs: films→films, series→series, mangas→mangas.
    /// </summary>
    public static string ListingUrl(string kind, int page)
    {
        var slug = kind.ToLowerInvariant() switch
        {
            "films" or "film" => "films",
            "series" or "serie" => "series",
            "mangas" or "manga" => "mangas",
            _ => kind.ToLowerInvariant(),
        };
        return $"{BaseUrl}/?p={slug}&page={Math.Max(1, page)}";
    }
}
