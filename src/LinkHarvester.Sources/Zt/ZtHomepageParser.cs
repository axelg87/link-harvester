using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using LinkHarvester.Core;

namespace LinkHarvester.Sources.Zt;

/// <summary>
/// Extracts RawListItems from a ZT homepage HTML document.
/// We parse the "div_list_news" rows because they expose:
///   - article URL
///   - human title
///   - quality hint (e.g. "BLU-RAY 1080p")
///   - language hint (e.g. "MULTI (TRUEFRENCH)")
/// All in one stable structure that has been the same for years.
/// </summary>
public sealed class ZtHomepageParser
{
    public const string SourceId = "zt";

    private static readonly Regex DetailRegex = new(
        "<div class=\"div_list_news\">\\s*" +
        "<a class=\"zt\" href=\"(?<href>[^\"]+)\" title=\"(?<title>[^\"]+)\"[^>]*>" +
        "(?<inner>.*?)" +
        "</div>",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex DetailReleaseRegex = new(
        "<span class=\"detail_release\"[^>]*><b>(?<text>[^<]+)</b></span>",
        RegexOptions.Compiled | RegexOptions.Singleline);

    public IReadOnlyList<RawListItem> Parse(string html)
    {
        var results = new List<RawListItem>();
        var seen = new HashSet<string>();

        foreach (Match m in DetailRegex.Matches(html))
        {
            var href = System.Net.WebUtility.HtmlDecode(m.Groups["href"].Value);
            var title = System.Net.WebUtility.HtmlDecode(m.Groups["title"].Value).Trim();
            var inner = m.Groups["inner"].Value;

            var parsed = ZtUrls.ParseArticle(href);
            if (parsed is null) continue;

            // Skip categories we explicitly don't want.
            var kind = parsed.Value.Kind;
            if (kind is "ebook" or "musique") continue;

            var releaseTags = new List<string>();
            foreach (Match rm in DetailReleaseRegex.Matches(inner))
            {
                releaseTags.Add(System.Net.WebUtility.HtmlDecode(rm.Groups["text"].Value).Trim());
            }

            string? qualityHint = null;
            string? languageHint = null;
            // Heuristic: tags that look like resolutions/sources are "quality"; tags that look
            // like languages/episodes are not.
            foreach (var raw in releaseTags)
            {
                var t = raw.Trim('(', ')').Trim();
                if (LooksLikeQuality(t) && qualityHint is null) qualityHint = t;
                else if (LooksLikeLanguage(t) && languageHint is null) languageHint = t;
            }

            var externalId = ZtUrls.ExternalId(parsed.Value.Kind, parsed.Value.Id);
            if (!seen.Add(externalId)) continue;

            results.Add(new RawListItem(
                SourceId: SourceId,
                ExternalId: externalId,
                Url: ZtUrls.Absolute(href),
                Title: title,
                QualityHint: qualityHint,
                LanguageHint: languageHint,
                PublishedAt: null));
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
