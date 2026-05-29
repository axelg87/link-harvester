using LinkHarvester.Persistence.Catalog;

namespace LinkHarvester.Persistence;

/// <summary>
/// Single source of truth for ranking link variants. Every place that
/// needs to pick "the best link" goes through this picker — the universal
/// search bar's inline SEND, /api/catalog/lookup, /api/discover, the
/// Telegram /find inline keyboard, the title detail's preselected links
/// for the modal, and the bulk-send missing-episode resolver.
///
/// Scoring (higher wins):
///   - Quality token: +1000 - (position × 50). list[0] beats list[1] by 50.
///   - Resolution: 2160p/4K/UHD → +60, 1080p → +30, 720p → +10.
///     The resolution delta is large enough that one tier of resolution
///     beats one tier of quality-token preference (50 vs 30 for 1080p vs
///     2160p), matching user expectation that "WEB-DL 4K" should beat
///     "BLURAY 1080p" when there's no remux on the table.
///   - Audio: +100 when AudioLangs contains the preferred token.
///   - Hoster: +(50 - position). list[0] beats list[1] by 1.
///   - Health: +20 Alive, −50 Dead.
///   - Size: tiebreaker only, up to +10 via bit-count curve.
///
/// Per-episode caller responsibility: filter the candidate list before
/// calling; this picker just ranks what it's given.
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
        => Score(
            c.QualityName,
            c.AudioLangs,
            c.NormalizedHost,
            c.HostName,
            c.HealthStatus,
            c.SizeBytes,
            hosterPriority,
            qualityPreference,
            audioPreference);

    /// <summary>
    /// Pure-data scoring overload. Used by callers that don't have a
    /// CatalogLinkEntity in hand (DTOs in the API + WASM client, etc.).
    /// Same algorithm; same ranking guarantees.
    /// </summary>
    public static int Score(
        string? qualityName,
        string? audioLangs,
        string? normalizedHost,
        string? hostName,
        string? healthStatus,
        long? sizeBytes,
        IReadOnlyList<string> hosterPriority,
        IReadOnlyList<string> qualityPreference,
        string audioPreference)
    {
        var score = 0;

        if (!string.IsNullOrEmpty(qualityName))
        {
            // Quality-token preference: WEB-DL > WEBRIP > HDTV, etc.
            var matchPos = -1;
            for (var i = 0; i < qualityPreference.Count; i++)
            {
                if (qualityName.Contains(qualityPreference[i], StringComparison.OrdinalIgnoreCase))
                {
                    matchPos = i;
                    break;
                }
            }
            if (matchPos >= 0) score += 1000 - matchPos * 50;

            // Resolution. ZT release strings use "4K" and "UHD" interchangeably
            // with "2160p"; without these aliases the resolution branch
            // missed every 4K release and the picker fell back on the size
            // tiebreaker, which is enough to lose to a same-host 1080p.
            if (qualityName.Contains("2160p", StringComparison.OrdinalIgnoreCase)
                || qualityName.Contains("4K", StringComparison.OrdinalIgnoreCase)
                || qualityName.Contains("UHD", StringComparison.OrdinalIgnoreCase))
                score += 60;
            else if (qualityName.Contains("1080p", StringComparison.OrdinalIgnoreCase))
                score += 30;
            else if (qualityName.Contains("720p", StringComparison.OrdinalIgnoreCase))
                score += 10;
        }

        if (!string.IsNullOrEmpty(audioLangs)
            && !string.IsNullOrWhiteSpace(audioPreference)
            && audioLangs.Contains(audioPreference, StringComparison.OrdinalIgnoreCase))
        {
            score += 100;
        }

        if (!string.IsNullOrEmpty(normalizedHost) || !string.IsNullOrEmpty(hostName))
        {
            for (var i = 0; i < hosterPriority.Count; i++)
            {
                if ((normalizedHost is { Length: > 0 } && string.Equals(hosterPriority[i], normalizedHost, StringComparison.OrdinalIgnoreCase))
                    || (hostName is { Length: > 0 } && string.Equals(hosterPriority[i], hostName, StringComparison.OrdinalIgnoreCase)))
                {
                    score += 50 - i;
                    break;
                }
            }
        }

        if (string.Equals(healthStatus, "Alive", StringComparison.OrdinalIgnoreCase)) score += 20;
        else if (string.Equals(healthStatus, "Dead", StringComparison.OrdinalIgnoreCase)) score -= 50;

        if (sizeBytes is long size && size > 0)
        {
            var bits = 0;
            for (var n = size; n > 0; n >>= 1) bits++;
            score += Math.Min(10, Math.Max(0, bits - 28));
        }
        return score;
    }
}

