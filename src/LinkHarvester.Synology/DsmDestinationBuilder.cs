using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace LinkHarvester.Synology;

/// <summary>
/// Builds the destination folder path under which a DSM download should land,
/// based on title kind + season number.
///
/// Layout produced (relative to the volume root, e.g. <c>video/</c>):
///   - Movies → <c>movieRoot</c> (flat, as before)
///   - Series → <c>seriesRoot/&lt;sanitized title&gt;/Season N</c>
///   - Anime  → <c>animeRoot /&lt;sanitized title&gt;/Season N</c>
///
/// When the season number is null or zero (e.g. miniseries with no S/E hint),
/// we skip the <c>Season N</c> level and stop at <c>&lt;root&gt;/&lt;title&gt;</c>.
/// </summary>
public static class DsmDestinationBuilder
{
    /// <summary>
    /// One destination path. <see cref="FullPath"/> is what gets passed to
    /// DownloadStation; <see cref="ParentSegments"/> are the intermediate
    /// folders we must ensure exist (created with FileStation.CreateFolder),
    /// in order from outermost to innermost.
    /// </summary>
    public sealed record DestinationPlan(string FullPath, IReadOnlyList<string> ParentSegments);

    public static DestinationPlan ForMovie(string movieRoot)
        => new(NormaliseRoot(movieRoot), Array.Empty<string>());

    public static DestinationPlan ForSeries(string seriesRoot, string title, int? seasonNumber)
        => Build(seriesRoot, title, seasonNumber);

    public static DestinationPlan ForAnime(string animeRoot, string title, int? seasonNumber)
        => Build(animeRoot, title, seasonNumber);

    private static DestinationPlan Build(string root, string title, int? seasonNumber)
    {
        var rootClean = NormaliseRoot(root);
        var titleClean = SanitizeFolderName(title);
        var titlePath = $"{rootClean}/{titleClean}";

        if (seasonNumber is int s && s > 0)
        {
            var seasonPath = $"{titlePath}/Season {s}";
            return new DestinationPlan(seasonPath, new[] { titlePath, seasonPath });
        }
        return new DestinationPlan(titlePath, new[] { titlePath });
    }

    private static string NormaliseRoot(string? root)
    {
        if (string.IsNullOrWhiteSpace(root)) return "video";
        var trimmed = root.Trim().Trim('/');
        return string.IsNullOrEmpty(trimmed) ? "video" : trimmed;
    }

    private static readonly char[] ReservedChars =
        { '\\', '/', ':', '*', '?', '"', '<', '>', '|' };

    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// Strip characters DSM/SMB/ext4 reject, drop diacritics, collapse runs of
    /// whitespace, trim leading dots, cap length. Empty/whitespace input
    /// produces "Unknown" so we never create a nameless folder.
    /// </summary>
    public static string SanitizeFolderName(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "Unknown";

        var nfkd = input.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(input.Length);
        foreach (var ch in nfkd)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            if (Array.IndexOf(ReservedChars, ch) >= 0) { sb.Append(' '); continue; }
            sb.Append(ch);
        }
        var cleaned = WhitespaceRegex.Replace(sb.ToString().Normalize(NormalizationForm.FormC), " ").Trim();
        cleaned = cleaned.TrimStart('.');

        if (cleaned.Length == 0) return "Unknown";
        if (cleaned.Length > 120) cleaned = cleaned[..120].TrimEnd();
        return cleaned;
    }
}
