using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using LinkHarvester.Core;

namespace LinkHarvester.Sources.Zt;

/// <summary>
/// Parses a ZT article page (movie, series, anime) into ArticleDetails.
///
/// Structural anchors (verified against captured fixtures):
///   - The article title is in &lt;h1&gt; near the top of the post body.
///   - The "Qualités également disponibles" section has &lt;a&gt; tags pointing
///     at sibling article URLs (one per other quality variant).
///   - The page contains exactly one aggregator dl-protect link with
///     ?fn=base64({kind}|{id}|{group}) -- repeated several times.
///   - "Liens De Téléchargement" header is followed by a postinfo &lt;div&gt;
///     containing per-hoster blocks. Each block has:
///         &lt;div style="...color:#xxxxxx"&gt;HosterName&lt;/div&gt;
///         &lt;a ...href="https://dl-protect.link/HASH?..."&gt;Telecharger|Episode N&lt;/a&gt;
///   - "Liens De Streaming" is a separate section we ignore (different rl=).
///   - Quality and language live in a sentence that starts with "Qualité ".
///   - For movies, "Taille du fichier" appears with the byte size.
///   - For series, "Episodes : N" appears in a header span.
/// </summary>
public sealed class ZtArticleParser
{
    private static readonly Regex DlProtectAggregatorRegex =
        new(@"https://dl-protect\.link/rqts-url\?fn=([^""'&\s]+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DlProtectHashedRegex =
        new(@"https://dl-protect\.link/(?<hash>[a-f0-9]{6,12})\?[^""'\s<]+",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex EpisodeLabelRegex =
        new(@"Episode\s*(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // "Qualité " (note the trailing space, no 's') followed by ":" or " " then a value, then "|" or "<"
    // This avoids matching "Qualités également disponibles" which has no following separator.
    private static readonly Regex QualityLineRegex =
        new(@"Qualité\s+(?:</u>\s*)?:\s*(?:</strong>\s*)?(?<q>[^|<\r\n]+?)\s*(?:\||<)", RegexOptions.Compiled);

    private static readonly Regex LanguageLineRegex =
        new(@"Langue\s+(?:</u>\s*)?:\s*(?:</strong>\s*)?(?<l>[^<\r\n]+?)\s*<", RegexOptions.Compiled);

    private static readonly Regex SizeLineRegex =
        new(@"Taille du fichier\s*</u>\s*:\s*</strong>\s*(?<n>[\d.,]+)\s*(?<u>Go|Mo|To|Ko|GB|MB|TB|KB)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex EpisodeCountRegex =
        new(@"(?<n>\d+)\s+Episodes?", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex InlineQualityFromHostersRegex =
        new(@"Qualité\s+(?<q>[^|<]+?)\s*\|\s*(?<l>[^<]+?)</div>", RegexOptions.Compiled);

    private readonly ZtTitleParser _titles;

    public ZtArticleParser(ZtTitleParser titles)
    {
        _titles = titles;
    }

    public ArticleDetails Parse(string url, string html)
    {
        var parsed = ZtUrls.ParseArticle(url) ?? throw new ArgumentException($"Not a ZT article URL: {url}");
        var kind = parsed.Kind switch
        {
            "film" => TitleKind.Movie,
            "serie" => TitleKind.Season,
            "manga" => TitleKind.Anime,
            _ => TitleKind.Other
        };

        var doc = new HtmlParser().ParseDocument(html);

        var rawTitle = ExtractDisplayTitle(doc);
        var (cleanTitle, year, season) = _titles.Parse(rawTitle, kind);

        var (quality, language) = ExtractQualityLanguage(html);

        long? sizeBytes = ExtractSizeBytes(html);
        int? episodeCount = ExtractEpisodeCount(html, doc);

        var aggregator = ExtractAggregatorDlProtect(html);
        var hosters = ExtractHosterLinks(html);
        var siblings = ExtractSiblingArticleUrls(doc);

        var contentHash = ComputeContentHash(aggregator, hosters);

        return new ArticleDetails(
            SourceId: ZtHomepageParser.SourceId,
            ExternalId: ZtUrls.ExternalId(parsed.Kind, parsed.Id),
            Url: url,
            Title: cleanTitle,
            Year: year,
            SeasonNumber: season,
            Kind: kind,
            QualityLabel: quality,
            Language: language,
            SizeBytes: sizeBytes,
            EpisodeCount: episodeCount,
            AggregatorDlProtectUrl: aggregator,
            Hosters: hosters,
            SiblingArticleUrls: siblings,
            ContentHash: contentHash);
    }

    private static string ExtractDisplayTitle(IHtmlDocument doc)
    {
        // The article body's first <h1> (not the chrome <h1>) is the title.
        // Heuristic: prefer an <h1> that doesn't contain "Télécharger" prefix only.
        var h1s = doc.QuerySelectorAll("h1").OfType<IElement>().ToList();
        foreach (var h in h1s)
        {
            var text = (h.TextContent ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(text)) continue;
            if (text.StartsWith("Télécharger ", StringComparison.Ordinal)) continue;
            return text;
        }

        // Fallback to <title>.
        var titleTag = doc.QuerySelector("title")?.TextContent ?? string.Empty;
        return titleTag.Replace("Télécharger ", string.Empty, StringComparison.Ordinal).Trim();
    }

    private static (string? quality, string? language) ExtractQualityLanguage(string html)
    {
        // First try "Qualité X | Y" inline form (used in postinfo header).
        var inline = InlineQualityFromHostersRegex.Match(html);
        if (inline.Success)
        {
            return (Clean(inline.Groups["q"].Value), Clean(inline.Groups["l"].Value));
        }

        var qm = QualityLineRegex.Match(html);
        var lm = LanguageLineRegex.Match(html);
        return (qm.Success ? Clean(qm.Groups["q"].Value) : null,
                lm.Success ? Clean(lm.Groups["l"].Value) : null);
    }

    private static string Clean(string s) =>
        System.Net.WebUtility.HtmlDecode(s)
            .Replace("\u00a0", " ")
            .Trim()
            .TrimEnd('|', ':')
            .Trim();

    private static long? ExtractSizeBytes(string html)
    {
        var m = SizeLineRegex.Match(html);
        if (!m.Success) return null;
        if (!double.TryParse(m.Groups["n"].Value.Replace(',', '.'),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var n)) return null;
        var unit = m.Groups["u"].Value.ToUpperInvariant();
        long mult = unit switch
        {
            "KO" or "KB" => 1024L,
            "MO" or "MB" => 1024L * 1024,
            "GO" or "GB" => 1024L * 1024 * 1024,
            "TO" or "TB" => 1024L * 1024 * 1024 * 1024,
            _ => 1
        };
        return (long)(n * mult);
    }

    private static int? ExtractEpisodeCount(string html, IHtmlDocument doc)
    {
        // 1. Try "(Episodes : N)" tag in series cards/headers.
        var episodesParen = Regex.Match(html, @"Episodes\s*:\s*(\d+)", RegexOptions.IgnoreCase);
        if (episodesParen.Success && int.TryParse(episodesParen.Groups[1].Value, out var n1))
            return n1;

        // 2. Or "8 Episodes" inline.
        var inline = EpisodeCountRegex.Match(doc.TextContent ?? string.Empty);
        if (inline.Success && int.TryParse(inline.Groups["n"].Value, out var n2))
            return n2;

        return null;
    }

    private static string ExtractAggregatorDlProtect(string html)
    {
        var m = DlProtectAggregatorRegex.Match(html);
        return m.Success ? m.Value : string.Empty;
    }

    /// <summary>
    /// Walks the html sequentially: every per-hoster block looks like
    /// `<div ...>HosterName</div> ... <a ... href="https://dl-protect.link/HASH?...">Episode 1|Telecharger</a>`
    /// possibly followed by more &lt;a&gt; tags for further episodes, until the next
    /// hoster name &lt;div&gt; or the "Liens De Streaming" section.
    ///
    /// We do this with a single pass by tracking the "current hoster" cursor as we
    /// scan known marker positions. Restricted to the **download** section only
    /// (everything before "Liens De Streaming" if present).
    /// </summary>
    private static IReadOnlyList<HosterLinks> ExtractHosterLinks(string html)
    {
        var dlEndIdx = html.IndexOf("Liens De Streaming", StringComparison.Ordinal);
        var section = dlEndIdx >= 0 ? html[..dlEndIdx] : html;

        var dlStart = section.IndexOf("Liens De T", StringComparison.Ordinal);
        if (dlStart < 0) return Array.Empty<HosterLinks>();
        section = section[dlStart..];

        var hosters = new List<HosterLinks>();
        var hosterNameRegex = new Regex(
            @"<div style=""font-weight:bold;color:#[0-9a-fA-F]{3,8}"">(?<name>[^<]+)</div>",
            RegexOptions.Compiled);

        var anchorRegex = new Regex(
            @"<a [^>]*href=""(?<url>https://dl-protect\.link/(?<hash>[a-f0-9]{6,12})\?[^""]+)""[^>]*>(?<txt>[^<]+)</a>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        var hosterMatches = hosterNameRegex.Matches(section);
        for (int i = 0; i < hosterMatches.Count; i++)
        {
            var nameMatch = hosterMatches[i];
            var name = System.Net.WebUtility.HtmlDecode(nameMatch.Groups["name"].Value).Trim();
            if (string.IsNullOrEmpty(name)) continue;

            int blockStart = nameMatch.Index + nameMatch.Length;
            int blockEnd = (i + 1 < hosterMatches.Count) ? hosterMatches[i + 1].Index : section.Length;
            var block = section[blockStart..blockEnd];

            var links = new List<ProtectedLink>();
            foreach (Match a in anchorRegex.Matches(block))
            {
                var url = System.Net.WebUtility.HtmlDecode(a.Groups["url"].Value);
                int? episodeIdx = null;
                var em = EpisodeLabelRegex.Match(a.Groups["txt"].Value);
                if (em.Success && int.TryParse(em.Groups[1].Value, out var ep)) episodeIdx = ep;
                links.Add(new ProtectedLink(url, episodeIdx));
            }

            if (links.Count > 0)
                hosters.Add(new HosterLinks(name, links));
        }

        return hosters;
    }

    private static IReadOnlyList<string> ExtractSiblingArticleUrls(IHtmlDocument doc)
    {
        var sibling = new List<string>();

        var section = doc.QuerySelectorAll("h3, h2")
            .OfType<IElement>()
            .FirstOrDefault(e => (e.TextContent ?? "").Contains("également disponibles", StringComparison.Ordinal));
        if (section is null) return sibling;

        // Variants are immediate-sibling <a> tags after the heading, until we hit
        // the next heading or a separator (e.g. div.smallsep).
        for (var walker = section.NextElementSibling; walker is not null; walker = walker.NextElementSibling)
        {
            if (walker.LocalName is "h2" or "h3") break;
            if ((walker.GetAttribute("class") ?? string.Empty).Contains("smallsep")) break;

            // The walker itself may be the <a>.
            if (walker is IHtmlAnchorElement anchor)
            {
                var href = anchor.GetAttribute("href") ?? string.Empty;
                if (ZtUrls.ParseArticle(href) is not null)
                    sibling.Add(ZtUrls.Absolute(href));
            }
            // Or contain anchors.
            foreach (var a in walker.QuerySelectorAll("a").OfType<IHtmlAnchorElement>())
            {
                var href = a.GetAttribute("href") ?? string.Empty;
                if (ZtUrls.ParseArticle(href) is not null)
                    sibling.Add(ZtUrls.Absolute(href));
            }
        }

        return sibling.Distinct().ToList();
    }

    private static string ComputeContentHash(string aggregator, IReadOnlyList<HosterLinks> hosters)
    {
        var sb = new StringBuilder();
        sb.Append(aggregator);
        sb.Append('|');
        foreach (var h in hosters.OrderBy(h => h.Hoster, StringComparer.Ordinal))
        {
            sb.Append(h.Hoster).Append(':');
            foreach (var l in h.Links.OrderBy(l => l.EpisodeIndex ?? 0).ThenBy(l => l.Url, StringComparer.Ordinal))
            {
                sb.Append(l.EpisodeIndex?.ToString() ?? "_").Append('=').Append(l.Url).Append(';');
            }
            sb.Append('|');
        }
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(bytes);
    }
}
