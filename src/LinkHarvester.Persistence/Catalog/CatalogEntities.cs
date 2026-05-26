namespace LinkHarvester.Persistence.Catalog;

/// <summary>
/// One row per movie or series-season aggregate. Many Links may point at the
/// same Title; for series, also many Episodes point at the same Title.
/// </summary>
public class CatalogTitleEntity
{
    public int Id { get; set; }

    /// <summary>External Hydracker title identity for de-duplication on re-ingest.</summary>
    public string CanonicalKey { get; set; } = string.Empty;

    public string TitleName { get; set; } = string.Empty;
    public string? OriginalTitle { get; set; }
    public string NormalizedTitle { get; set; } = string.Empty;

    public string? ImdbId { get; set; }
    public int? TmdbId { get; set; }

    public string CategoryName { get; set; } = string.Empty;
    public string? TitlePoster { get; set; }

    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }

    public int LinkCount { get; set; }
    public int EpisodeCount { get; set; }

    /// <summary>
    /// Soft-delete flag — when every link for this title is verifiably
    /// <see cref="CatalogLinkEntity.HealthStatus"/> = "Dead", the health-sweep
    /// marks the title hidden. The UI filters them out by default; toggling
    /// "show hidden" reveals them for audit. We never hard-delete.
    /// </summary>
    public bool IsHidden { get; set; }
    public string? HiddenReason { get; set; }

    public CatalogTitleMetadataEntity? Metadata { get; set; }
    public List<CatalogEpisodeEntity> Episodes { get; set; } = new();
    public List<CatalogLinkEntity> Links { get; set; } = new();
}

public class CatalogEpisodeEntity
{
    public int Id { get; set; }
    public int TitleId { get; set; }
    public CatalogTitleEntity Title { get; set; } = null!;

    public int SeasonNumber { get; set; }
    public int EpisodeNumber { get; set; }
    public string? EpisodeName { get; set; }
    public string? EpisodePoster { get; set; }
    public bool IsFullSeason { get; set; }

    public List<CatalogLinkEntity> Links { get; set; } = new();
}

public class CatalogLinkEntity
{
    public int Id { get; set; }

    /// <summary>External Hydracker link_id, unique in the dataset.</summary>
    public long ExternalLinkId { get; set; }

    public int TitleId { get; set; }
    public CatalogTitleEntity Title { get; set; } = null!;

    public int? EpisodeId { get; set; }
    public CatalogEpisodeEntity? Episode { get; set; }

    public string LinkUrl { get; set; } = string.Empty;
    public string LinkSource { get; set; } = "catalog";
    public int? HarvesterArticleId { get; set; }
    public string HostName { get; set; } = string.Empty;
    public string NormalizedHost { get; set; } = string.Empty;
    public string? QualityName { get; set; }
    public string? AudioLangs { get; set; }
    public string? SubLangs { get; set; }
    public long? SizeBytes { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Denormalised result of the most recent health check.
    /// One of: <c>"Alive"</c>, <c>"Dead"</c>, <c>"Unknown"</c>, <c>null</c> (never checked).
    /// </summary>
    public string? HealthStatus { get; set; }
    public DateTimeOffset? HealthCheckedAt { get; set; }
    public string? HealthSignature { get; set; }
}

public class CatalogTitleMetadataEntity
{
    public int Id { get; set; }
    public int TitleId { get; set; }
    public CatalogTitleEntity Title { get; set; } = null!;

    public int? TmdbId { get; set; }
    public string? ImdbId { get; set; }
    public string? ReleaseDate { get; set; }
    public int? Year { get; set; }
    public int? Runtime { get; set; }
    public double? VoteAverage { get; set; }
    public int? VoteCount { get; set; }
    public double? Popularity { get; set; }
    public string GenresJson { get; set; } = "[]";
    public string? OriginalLanguage { get; set; }
    public string? Overview { get; set; }
    public string? Status { get; set; }

    public string EnrichmentSource { get; set; } = "pending";
    public bool MetadataUncertain { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset? LastEnrichedAt { get; set; }
}

public class CatalogImportRunEntity
{
    public int Id { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string Source { get; set; } = "upload";
    public string? SourceDescription { get; set; }
    public long TotalRecords { get; set; }
    public long InsertedLinks { get; set; }
    public long InsertedTitles { get; set; }
    public long InsertedEpisodes { get; set; }
    public long FailedRecords { get; set; }
    public string Status { get; set; } = "running";
    public string? Notes { get; set; }
}
