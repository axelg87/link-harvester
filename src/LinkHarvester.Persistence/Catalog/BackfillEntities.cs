namespace LinkHarvester.Persistence.Catalog;

/// <summary>
/// One row per backfill execution (resumable). On crash/restart we can pick
/// up at <see cref="LastCompletedPage"/> + 1 — re-scanning that page is safe
/// thanks to dedup on Articles.ExternalId.
/// </summary>
public class BackfillRunEntity
{
    public int Id { get; set; }

    /// <summary>Source identifier (e.g. "zt").</summary>
    public string SourceId { get; set; } = string.Empty;

    /// <summary>Kind we walked: "films", "series", "mangas".</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Articles published on/after this cutoff are considered.</summary>
    public DateTimeOffset FromDate { get; set; }

    public int StartPage { get; set; } = 1;
    public int LastCompletedPage { get; set; }
    public string? LastSeenArticleExternalId { get; set; }

    public int Discovered { get; set; }
    public int Healthy { get; set; }
    public int Enriched { get; set; }
    public int Promoted { get; set; }
    public int Skipped { get; set; }

    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>"running", "succeeded", "failed", "cancelled".</summary>
    public string Status { get; set; } = "running";
    public string? Error { get; set; }
}

/// <summary>
/// One row per health-sweep execution (manual trigger).
/// </summary>
public class HealthSweepRunEntity
{
    public int Id { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>Inclusive lower bound on CatalogLinks.Id we resumed from.</summary>
    public int LastCheckedCatalogLinkId { get; set; }

    public int Checked { get; set; }
    public int Alive { get; set; }
    public int Dead { get; set; }
    public int Unknown { get; set; }
    public int HiddenTitles { get; set; }

    /// <summary>Optional restriction: only sweep links with this hoster (case-insensitive).</summary>
    public string? HosterFilter { get; set; }

    public string Status { get; set; } = "running";
    public string? Error { get; set; }
}
