using System.Globalization;
using System.Text.RegularExpressions;

namespace LinkHarvester.Sources.Zt;

/// <summary>
/// Parses a ZT French publish date like "26 mai 2026" or "9 juillet 2025"
/// into a UTC <see cref="DateTimeOffset"/>.
/// </summary>
public static class ZtFrenchDate
{
    private static readonly Regex Pattern = new(
        @"(?<d>\d{1,2})\s+(?<m>janvier|f[ée]vrier|mars|avril|mai|juin|juillet|ao[ûu]t|septembre|octobre|novembre|d[ée]cembre)\s+(?<y>20\d{2})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Dictionary<string, int> Months = new(StringComparer.OrdinalIgnoreCase)
    {
        ["janvier"] = 1,
        ["fevrier"] = 2, ["février"] = 2,
        ["mars"] = 3,
        ["avril"] = 4,
        ["mai"] = 5,
        ["juin"] = 6,
        ["juillet"] = 7,
        ["aout"] = 8, ["août"] = 8,
        ["septembre"] = 9,
        ["octobre"] = 10,
        ["novembre"] = 11,
        ["decembre"] = 12, ["décembre"] = 12,
    };

    public static DateTimeOffset? TryParse(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var m = Pattern.Match(input);
        if (!m.Success) return null;
        if (!int.TryParse(m.Groups["d"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d)) return null;
        if (!int.TryParse(m.Groups["y"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var y)) return null;
        if (!Months.TryGetValue(m.Groups["m"].Value, out var mm)) return null;
        try
        {
            return new DateTimeOffset(y, mm, d, 0, 0, 0, TimeSpan.Zero);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
