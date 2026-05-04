using LinkHarvester.Core;

namespace LinkHarvester.Persistence;

public class TitleEntity
{
    public int Id { get; set; }
    public string Canonical { get; set; } = string.Empty;
    public string NormalizedTitle { get; set; } = string.Empty;
    public string DisplayTitle { get; set; } = string.Empty;
    public int? Year { get; set; }
    public TitleKind Kind { get; set; }
    public int? SeasonNumber { get; set; }

    public TitleStatus Status { get; set; }
    public int? BestSubmittedQualityScore { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public List<ArticleEntity> Articles { get; set; } = new();
}

public class ArticleEntity
{
    public int Id { get; set; }

    public int TitleId { get; set; }
    public TitleEntity Title { get; set; } = null!;

    public string SourceId { get; set; } = string.Empty;
    public string ExternalId { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;

    public string DisplayTitle { get; set; } = string.Empty;
    public string? QualityLabel { get; set; }
    public int QualityScore { get; set; }
    public string? Resolution { get; set; }
    public string? Codec { get; set; }
    public string? SourceTier { get; set; }
    public string? Language { get; set; }
    public long? SizeBytes { get; set; }
    public int? EpisodeCount { get; set; }

    public string AggregatorDlProtectUrl { get; set; } = string.Empty;
    /// <summary>JSON-serialized List&lt;HosterLinks&gt;.</summary>
    public string HostersJson { get; set; } = "[]";

    public string ContentHash { get; set; } = string.Empty;
    public ArticleStatus Status { get; set; }
    public string? FailureReason { get; set; }
    public int ResolutionAttempts { get; set; }

    public DateTimeOffset DiscoveredAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public DateTimeOffset? SubmittedAt { get; set; }

    public List<ResolvedLinkEntity> ResolvedLinks { get; set; } = new();
}

public class ResolvedLinkEntity
{
    public int Id { get; set; }
    public int ArticleId { get; set; }
    public ArticleEntity Article { get; set; } = null!;

    public string Hoster { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public int? EpisodeIndex { get; set; }
    public DateTimeOffset ResolvedAt { get; set; }
}

public class SubmissionEntity
{
    public int Id { get; set; }
    public int ArticleId { get; set; }
    public ArticleEntity Article { get; set; } = null!;
    public string SubmittedUrlsJson { get; set; } = "[]";
    public string DsmTaskIdsJson { get; set; } = "[]";
    public SubmissionStatus Status { get; set; }
    public string? ResponseMessage { get; set; }
    public DateTimeOffset SubmittedAt { get; set; }
}

public class ScanRunEntity
{
    public int Id { get; set; }
    public string SourceId { get; set; } = string.Empty;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public int Discovered { get; set; }
    public int NewArticles { get; set; }
    public int Resolved { get; set; }
    public int Failed { get; set; }
    public string? Notes { get; set; }
}

public class CapSolverSpendEntity
{
    public int Id { get; set; }
    public int Year { get; set; }
    public int Month { get; set; }
    public decimal SpentUsd { get; set; }
    public int Calls { get; set; }
}
