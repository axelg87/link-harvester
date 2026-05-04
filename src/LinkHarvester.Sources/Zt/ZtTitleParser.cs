using System.Text.RegularExpressions;
using LinkHarvester.Core;

namespace LinkHarvester.Sources.Zt;

/// <summary>
/// Extracts the human title, year and season number from ZT-style display titles.
/// Examples:
///   "The Boys - Saison 5"             -> ("The Boys", null, 5, Season)
///   "Euphoria (2019) - Saison 3"      -> ("Euphoria", 2019, 3, Season)
///   "Los Tigres"                      -> ("Los Tigres", null, null, Movie)  [year comes from elsewhere]
/// </summary>
public sealed class ZtTitleParser
{
    private static readonly Regex SeasonRegex =
        new(@"\s*-\s*Saison\s*(?<n>\d+)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex YearInTitleRegex =
        new(@"\s*\((?<y>(19|20)\d{2})\)\s*", RegexOptions.Compiled);

    public (string CleanTitle, int? Year, int? SeasonNumber) Parse(string raw, TitleKind kind)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (string.Empty, null, null);

        var s = System.Net.WebUtility.HtmlDecode(raw).Trim();
        // Collapse the trailing whitespace/tabs ZT loves to put after titles.
        s = Regex.Replace(s, @"\s+", " ");

        int? season = null;
        var sm = SeasonRegex.Match(s);
        if (sm.Success)
        {
            season = int.Parse(sm.Groups["n"].Value);
            s = s[..sm.Index];
        }

        int? year = null;
        var ym = YearInTitleRegex.Match(s);
        if (ym.Success)
        {
            year = int.Parse(ym.Groups["y"].Value);
            s = (s[..ym.Index] + s[(ym.Index + ym.Length)..]).Trim();
        }

        if (kind == TitleKind.Season && season is null)
        {
            // ZT sometimes omits the season number; treat as season 1.
            season = 1;
        }

        return (s.Trim(), year, season);
    }
}
