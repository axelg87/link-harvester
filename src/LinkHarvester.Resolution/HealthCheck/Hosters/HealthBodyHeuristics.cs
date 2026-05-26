using System.Text.RegularExpressions;

namespace LinkHarvester.Resolution.HealthCheck.Hosters;

/// <summary>
/// Shared body-inspection helpers used by per-host matchers.
/// </summary>
internal static class HealthBodyHeuristics
{
    private static readonly Regex TitleRegex = new(
        "<title[^>]*>(?<t>[^<]{1,300})</title>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Look for typical media-file artefacts: an extension, or quality/codec
    /// tokens that nearly always accompany a release name.
    /// </summary>
    private static readonly Regex FilenameLikeRegex = new(
        @"\.(mkv|mp4|avi|m4v|wmv|mov|mp3|flac|m4a|wav|iso|zip|rar|7z)|" +
        @"(?:^|\W)(1080p|720p|2160p|4k|hdtv|webrip|web-?dl|hdlight|hdcam|bluray|blu-ray|x264|x265|h\.?264|h\.?265|hevc|aac|ac3|multi|truefrench|french|vostfr|dvdrip)(?:\W|$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static string? ExtractTitle(string? body)
    {
        if (string.IsNullOrEmpty(body)) return null;
        var m = TitleRegex.Match(body);
        return m.Success ? m.Groups["t"].Value.Trim() : null;
    }

    /// <summary>
    /// True when the given string looks like it contains a media filename or
    /// quality/codec token — strong evidence the hoster's page is rendering
    /// real file metadata, i.e. the file is alive.
    /// </summary>
    public static bool LooksLikeFilename(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        // Strip generic host-branding tail to avoid false positives like
        // "Download | Uploady.io" being treated as alive.
        var trimmed = s.Trim();
        // Require some minimum content beyond a stock prefix.
        if (trimmed.Length < 12) return false;
        return FilenameLikeRegex.IsMatch(trimmed);
    }

    public static bool HasFilenameInTitle(string? body) =>
        LooksLikeFilename(ExtractTitle(body));
}
