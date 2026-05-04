using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace LinkHarvester.Core;

/// <summary>
/// Stable normalization of a title string used for cross-article matching.
/// Lowercase + diacritics stripped + non-alphanumerics collapsed to single space.
/// </summary>
public sealed class TitleNormalizer : ITitleNormalizer
{
    private static readonly Regex NonAlphanumeric = new(@"[^a-z0-9]+", RegexOptions.Compiled);
    private static readonly Regex MultiSpace = new(@"\s+", RegexOptions.Compiled);

    public string Normalize(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        var lowered = raw.ToLowerInvariant();
        var stripped = StripDiacritics(lowered);
        var spaced = NonAlphanumeric.Replace(stripped, " ");
        var collapsed = MultiSpace.Replace(spaced, " ").Trim();
        return collapsed;
    }

    private static string StripDiacritics(string s)
    {
        var normalized = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}
