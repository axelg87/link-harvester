using LinkHarvester.Persistence.Catalog;

namespace LinkHarvester.Persistence;

/// <summary>
/// Picks the single "best" link for a title given user preferences. Used
/// wherever the system needs one deterministic variant — the universal
/// search bar's inline SEND, the Telegram /find inline keyboard, the
/// Following page's "auto-grab" path.
///
/// Scoring (higher wins):
///   - Quality:   +1000 - position-in-preference-list (so list[0] beats list[1]).
///                Resolution-only tokens (2160p > 1080p > 720p) also rank.
///   - Audio:     +100 when AudioLangs contains the preferred token.
///   - Hoster:    +(50 - position-in-priority-list).
///   - Health:    +20 when HealthStatus = "Alive"; -50 when "Dead".
///   - Size:      tiebreaker, larger wins (cap +10).
///
/// Episodes vs full-season packs: callers are responsible for filtering
/// the candidate set; this method just ranks the set it's given.
/// </summary>
public static class BestVariantPicker
{
    public static CatalogLinkEntity? Pick(
        IEnumerable<CatalogLinkEntity> candidates,
        IReadOnlyList<string> hosterPriority,
        IReadOnlyList<string> qualityPreference,
        string audioPreference)
    {
        CatalogLinkEntity? best = null;
        var bestScore = int.MinValue;
        foreach (var c in candidates)
        {
            var score = Score(c, hosterPriority, qualityPreference, audioPreference);
            if (score > bestScore)
            {
                bestScore = score;
                best = c;
            }
        }
        return best;
    }

    public static int Score(
        CatalogLinkEntity c,
        IReadOnlyList<string> hosterPriority,
        IReadOnlyList<string> qualityPreference,
        string audioPreference)
    {
        var score = 0;

        if (!string.IsNullOrEmpty(c.QualityName))
        {
            // Match the most specific preference token contained in the quality name.
            var matchPos = -1;
            for (var i = 0; i < qualityPreference.Count; i++)
            {
                if (c.QualityName.Contains(qualityPreference[i], StringComparison.OrdinalIgnoreCase))
                {
                    matchPos = i;
                    break;
                }
            }
            if (matchPos >= 0) score += 1000 - matchPos * 50;

            // Resolution component, independent of source token.
            if (c.QualityName.Contains("2160p", StringComparison.OrdinalIgnoreCase)) score += 30;
            else if (c.QualityName.Contains("1080p", StringComparison.OrdinalIgnoreCase)) score += 20;
            else if (c.QualityName.Contains("720p", StringComparison.OrdinalIgnoreCase)) score += 10;
        }

        if (!string.IsNullOrEmpty(c.AudioLangs)
            && !string.IsNullOrWhiteSpace(audioPreference)
            && c.AudioLangs.Contains(audioPreference, StringComparison.OrdinalIgnoreCase))
        {
            score += 100;
        }

        if (!string.IsNullOrEmpty(c.NormalizedHost))
        {
            for (var i = 0; i < hosterPriority.Count; i++)
            {
                if (string.Equals(hosterPriority[i], c.NormalizedHost, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(hosterPriority[i], c.HostName, StringComparison.OrdinalIgnoreCase))
                {
                    score += 50 - i;
                    break;
                }
            }
        }

        if (string.Equals(c.HealthStatus, "Alive", StringComparison.OrdinalIgnoreCase)) score += 20;
        else if (string.Equals(c.HealthStatus, "Dead", StringComparison.OrdinalIgnoreCase)) score -= 50;

        if (c.SizeBytes is long size && size > 0)
        {
            // Up to +10 for a 50GB link, log-ish curve via bit count.
            var bits = 0;
            for (var n = size; n > 0; n >>= 1) bits++;
            score += Math.Min(10, Math.Max(0, bits - 28));
        }
        return score;
    }
}
