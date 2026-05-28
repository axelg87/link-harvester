namespace LinkHarvester.Persistence.Catalog;

/// <summary>
/// User-driven hide for a catalog title in the Following page. Following is
/// otherwise derived from submission history with no time decay; dismissal is
/// the only way to remove a show from view once you've grabbed any episode.
/// Re-submitting any episode of the dismissed title will not auto-restore —
/// dismissal sticks until manually cleared.
/// </summary>
public class FollowingDismissalEntity
{
    public int Id { get; set; }
    public int CatalogTitleId { get; set; }
    public DateTimeOffset DismissedAt { get; set; }
}

/// <summary>
/// Append-only log of "newly ingested article brings an episode the user has
/// not yet submitted for a title in their Following set." Data-only signal —
/// the in-app UI never surfaces these rows. The Telegram notification
/// dispatcher polls for ConsumedAt = null, sends a message, and marks consumed.
/// </summary>
public class FollowingDetectionLogEntity
{
    public int Id { get; set; }
    public int CatalogTitleId { get; set; }
    /// <summary>Compact "S02E06" code, or "S02" for full-season packs.</summary>
    public string EpisodeCode { get; set; } = string.Empty;
    public int? ArticleId { get; set; }
    public int? CatalogLinkId { get; set; }
    public DateTimeOffset DetectedAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
    public string? ConsumedBy { get; set; }
}

/// <summary>
/// One row per (external popularity source, ranked title) pair. Refreshed
/// nightly by DiscoveryRefreshWorker. The catalog-gating rule (only rows
/// whose CatalogTitleId resolves to a real catalog entry) is enforced at
/// insert time — so any row in this table is guaranteed downloadable.
/// </summary>
public class DiscoveryEntryEntity
{
    public int Id { get; set; }
    public int CatalogTitleId { get; set; }
    /// <summary>Source identifier, e.g. "tmdb_top_rated_movie", "trakt_trending_week", "letterboxd_popular_week".</summary>
    public string Source { get; set; } = string.Empty;
    /// <summary>1-based rank within the source list at fetch time.</summary>
    public int Rank { get; set; }
    /// <summary>Human-readable label rendered in the UI badge.</summary>
    public string Reason { get; set; } = string.Empty;
    public DateTimeOffset FetchedAt { get; set; }
}
