using System.Text.RegularExpressions;

namespace LinkHarvester.Core;

/// <summary>
/// Maps a free-form quality label (e.g. "WEB-DL 4K", "BLU-RAY 1080p", "WEBRIP 720p (TRUEFRENCH)")
/// into a score used to pick the "best" variant within a title.
/// Resolution dominates; codec and source tier add small bonuses.
/// </summary>
public sealed class QualityScorer : IQualityScorer
{
    public QualityRating Score(string? qualityLabel, string? language)
    {
        var label = (qualityLabel ?? string.Empty).ToUpperInvariant();
        var lang = (language ?? string.Empty).ToUpperInvariant();

        var (resolution, resolutionScore) = DetectResolution(label);
        var (codec, codecBonus) = DetectCodec(label);
        var (source, sourceBonus) = DetectSource(label);
        var langBonus = DetectLanguageBonus(label, lang);

        var total = resolutionScore + codecBonus + sourceBonus + langBonus;
        return new QualityRating(total, resolution, codec, source, lang.Length == 0 ? null : lang);
    }

    private static (string label, int score) DetectResolution(string label)
    {
        if (Regex.IsMatch(label, @"\b(4K|2160P|UHD)\b")) return ("4K", 1000);
        if (Regex.IsMatch(label, @"\b1080P?\b")) return ("1080p", 800);
        if (Regex.IsMatch(label, @"\b720P?\b")) return ("720p", 600);
        if (Regex.IsMatch(label, @"\b(480P|SD)\b")) return ("480p", 400);
        // Bare "WEBRIP" with no resolution at ZT typically means SD-tier.
        if (Regex.IsMatch(label, @"\bWEBRIP\b") && !Regex.IsMatch(label, @"\d{3,4}P?")) return ("WEBRIP", 500);
        return ("Unknown", 100);
    }

    private static (string? codec, int bonus) DetectCodec(string label)
    {
        if (Regex.IsMatch(label, @"\b(X265|H265|HEVC)\b")) return ("x265", 20);
        if (Regex.IsMatch(label, @"\b(X264|H264|AVC)\b")) return ("x264", 10);
        return (null, 0);
    }

    private static (string? source, int bonus) DetectSource(string label)
    {
        if (Regex.IsMatch(label, @"\bBLU-?RAY\b")) return ("BluRay", 30);
        if (Regex.IsMatch(label, @"\bWEB-?DL\b")) return ("WEB-DL", 20);
        if (Regex.IsMatch(label, @"\bHDLIGHT\b")) return ("HDLight", 15);
        if (Regex.IsMatch(label, @"\bWEBRIP\b")) return ("WEBRip", 10);
        if (Regex.IsMatch(label, @"\bHDTV\b")) return ("HDTV", 5);
        return (null, 0);
    }

    private static int DetectLanguageBonus(string label, string lang)
    {
        var combined = label + " " + lang;
        if (combined.Contains("MULTI") || combined.Contains("TRUEFRENCH")) return 5;
        if (combined.Contains("FRENCH") || combined.Contains("VFF")) return 3;
        if (combined.Contains("VOSTFR")) return 1;
        return 0;
    }
}
