using System.Text.RegularExpressions;
using LinkHarvester.Core;

namespace LinkHarvester.Sources.Zt;

/// <summary>
/// Parses a ZT *category listing* page (e.g. <c>/?p=films&amp;page=N</c>) into
/// <see cref="RawListItem"/>s — with the publish date populated from the
/// <c>&lt;time&gt;</c> tag, which only the listing page exposes.
///
/// Layout (verified against captured fixtures):
///   &lt;div class="cover_global" ...&gt;
///     &lt;div ...&gt;Publié le &lt;time&gt;26 mai 2026&lt;/time&gt;&lt;/div&gt;
///     &lt;a href="?p=film&amp;id=NNNNN-slug"&gt;&lt;img ...&gt;&lt;/a&gt;
///     &lt;div class="cover_infos_global"&gt;
///       &lt;div class="cover_infos_title"&gt;
///         &lt;a href="?p=film&amp;id=NNNNN-slug"&gt;Display Title&lt;/a&gt;
///         &lt;span class="detail_release ..."&gt;... quality / language ...&lt;/span&gt;
/// </summary>
public sealed class ZtListingParser
{
    public const string SourceId = "zt";

    private static readonly Regex CardRegex = new(
        "<div class=\"cover_global\"[^>]*>(?<card>.*?)</div>\\s*</div>\\s*</div>",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex DateRegex = new(
        "<time[^>]*>(?<text>[^<]+)</time>",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex TitleLinkRegex = new(
        "<a\\s+href=\"(?<href>\\?p=(?:film|serie|manga)[^\"]+)\"[^>]*>(?<title>[^<]+)</a>",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex AnyArticleHrefRegex = new(
        "href=\"(?<href>\\?p=(?:film|serie|manga)[^\"]+)\"",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex DetailReleaseRegex = new(
        "<span class=\"detail_release[^\"]*\"[^>]*>(?<inner>.*?)</span>\\s*<br",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex ReleaseTagRegex = new(
        "<b>(?<text>[^<]+)</b>",
        RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>
    /// Parse one category-listing HTML page into items.
    /// Items keep the order they appear in the page (ZT renders newest-first).
    /// </summary>
    public IReadOnlyList<RawListItem> Parse(string html)
    {
        if (string.IsNullOrEmpty(html)) return Array.Empty<RawListItem>();

        var results = new List<RawListItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match card in CardRegex.Matches(html))
        {
            var cardHtml = card.Groups["card"].Value;

            // Find the article URL: prefer the link that wraps the display title (has text content),
            // otherwise fall back to any ?p=film|serie|manga href on the card (img-only link).
            string? href = null;
            string? title = null;

            var titleLink = TitleLinkRegex.Match(cardHtml);
            if (titleLink.Success)
            {
                href = System.Net.WebUtility.HtmlDecode(titleLink.Groups["href"].Value);
                title = System.Net.WebUtility.HtmlDecode(titleLink.Groups["title"].Value).Trim();
            }
            else
            {
                var fallback = AnyArticleHrefRegex.Match(cardHtml);
                if (fallback.Success)
                    href = System.Net.WebUtility.HtmlDecode(fallback.Groups["href"].Value);
            }

            if (string.IsNullOrEmpty(href)) continue;
            var parsed = ZtUrls.ParseArticle(href);
            if (parsed is null) continue;

            // Skip categories we explicitly don't want.
            var kind = parsed.Value.Kind;
            if (kind is "ebook" or "musique" or "jeu") continue;

            var externalId = ZtUrls.ExternalId(kind, parsed.Value.Id);
            if (!seen.Add(externalId)) continue;

            // Date from <time>...</time>
            DateTimeOffset? publishedAt = null;
            var dm = DateRegex.Match(cardHtml);
            if (dm.Success)
                publishedAt = ZtFrenchDate.TryParse(dm.Groups["text"].Value);

            // Release tags (quality + language) — same heuristic as homepage parser.
            string? qualityHint = null;
            string? languageHint = null;
            var dr = DetailReleaseRegex.Match(cardHtml);
            if (dr.Success)
            {
                foreach (Match rm in ReleaseTagRegex.Matches(dr.Groups["inner"].Value))
                {
                    var t = System.Net.WebUtility.HtmlDecode(rm.Groups["text"].Value).Trim().Trim('(', ')').Trim();
                    if (LooksLikeQuality(t) && qualityHint is null) qualityHint = t;
                    else if (LooksLikeLanguage(t) && languageHint is null) languageHint = t;
                }
            }

            results.Add(new RawListItem(
                SourceId: SourceId,
                ExternalId: externalId,
                Url: ZtUrls.Absolute(href),
                Title: string.IsNullOrEmpty(title) ? null : title,
                QualityHint: qualityHint,
                LanguageHint: languageHint,
                PublishedAt: publishedAt));
        }

        return results;
    }

    private static bool LooksLikeQuality(string t)
    {
        var u = t.ToUpperInvariant();
        return u.Contains("4K") || u.Contains("1080") || u.Contains("720") ||
               u.Contains("WEBRIP") || u.Contains("WEB-DL") || u.Contains("BLU-RAY") ||
               u.Contains("HDLIGHT") || u.Contains("HDTV") || u.Contains("UHD");
    }

    private static bool LooksLikeLanguage(string t)
    {
        var u = t.ToUpperInvariant();
        return u.Contains("MULTI") || u.Contains("TRUEFRENCH") || u.Contains("FRENCH") ||
               u.Contains("VOSTFR") || u.Contains("VFF") || u.Contains("VF ") || u == "VF";
    }
}
