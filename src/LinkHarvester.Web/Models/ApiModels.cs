namespace LinkHarvester.Web.Models;

public sealed record InboxCard(
    int TitleId,
    int? CatalogTitleId,
    string DisplayTitle,
    string? Poster,
    int? Year,
    double? Rating,
    List<string> Genres,
    bool MetadataUncertain,
    string Kind,
    int? SeasonNumber,
    bool BetterAvailable,
    DateTimeOffset UpdatedAt,
    ArticleSummary Chosen,
    List<ArticleSummary> OtherVariants);

public sealed record ArticleSummary(
    int Id,
    string Url,
    string QualityLabel,
    int QualityScore,
    string? Resolution,
    string? Codec,
    string? SourceTier,
    string? Language,
    long? SizeBytes,
    int? EpisodeCount,
    List<HosterSummary> Hosters,
    string Status);

public sealed record HosterSummary(string Hoster, int LinkCount);

public sealed record ScanRun(
    int Id, string SourceId, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt,
    int Discovered, int NewArticles, int Resolved, int Failed, string? Notes);

public sealed record SendResult(int SubmissionId, string Status, string? Response, List<string> TaskIds);

public sealed record BudgetSnapshot(decimal SpentUsd, int Calls);

public sealed record Settings(
    string SynologyBaseUrl, string SynologyConnectionMode, string SynologyQuickConnectId,
    string SynologyResolvedBaseUrl, DateTimeOffset? SynologyResolvedAt,
    string SynologyUsername, bool SynologyPasswordSet,
    string? SynologyOtpCode, string SynologyMovieDestination, string SynologySeriesDestination,
    string SynologyAnimeDestination,
    int ScanIntervalMinutes, bool ScanOnStartup, List<string> HosterPriority,
    string AuthUsername, bool AuthPasswordSet,
    bool TmdbApiKeySet, bool TmdbEnrichmentEnabled, int TmdbEnrichmentConcurrency,
    string PlexBaseUrl, bool PlexTokenSet,
    List<string> QualityPreference, string AudioPreference,
    bool TraktClientIdSet, bool TelegramBotTokenSet, long TelegramOwnerChatId);

public sealed record UpdateSettings(
    string? SynologyBaseUrl, string? SynologyConnectionMode, string? SynologyQuickConnectId,
    string? SynologyUsername, string? SynologyPassword,
    string? SynologyOtpCode, string? SynologyMovieDestination, string? SynologySeriesDestination,
    string? SynologyAnimeDestination,
    int? ScanIntervalMinutes, bool? ScanOnStartup, List<string>? HosterPriority,
    string? AuthUsername, string? AuthPassword,
    string? TmdbApiKey, bool TmdbEnrichmentEnabled, int TmdbEnrichmentConcurrency,
    string? PlexBaseUrl, string? PlexToken,
    List<string>? QualityPreference = null, string? AudioPreference = null,
    string? TraktClientId = null, string? TelegramBotToken = null, long? TelegramOwnerChatId = null);

public sealed record SynologyTestResult(bool Ok, string? Error);

public sealed record QuickConnectResolveResult(
    bool Ok,
    string? BaseUrl,
    DateTimeOffset? ResolvedAt,
    List<string> ProbedUrls,
    string? Error);

public sealed record SendHistoryPage(int Total, int Page, int PageSize, List<SendAttemptSummary> Items);

public sealed record SendAttemptSummary(
    int Id,
    string Source,
    string Status,
    string DisplayTitle,
    int UrlCount,
    DateTimeOffset SubmittedAt,
    DateTimeOffset? CompletedAt,
    string? ResponseMessage,
    int? ResendOfSubmissionId);

public sealed record SendResendResult(int SubmissionId, string Status, string? Response, List<string> TaskIds);

// ── Catalog ──────────────────────────────────────────────────────────────
public sealed record CatalogStats(
    int Titles,
    int Links,
    int Episodes,
    int Enriched,
    int EnrichmentFailed,
    int EnrichmentFailedTransient,
    string EnrichmentRunState);

public sealed record ResetFailedResult(int Reset);

public sealed record FacetEntry(string Key, int Count);
public sealed record CatalogFacets(List<FacetEntry> Categories, List<FacetEntry> Hosts, List<FacetEntry> Qualities, List<FacetEntry> Audio);

public sealed record SearchHit(
    int Id, string Title, string? OriginalTitle, string Category, string? Poster,
    int LinkCount, int EpisodeCount, int? Year, double? Rating, int? Runtime,
    string? Overview, List<string> Genres, string? OriginalLanguage, string? EnrichmentSource,
    bool MetadataUncertain, int? SeasonNumber);

public sealed record InventoryFile(string Name, long? SizeBytes, DateTimeOffset? ModifiedAt);

public sealed record InventoryResult(
    bool Ok, string? Error, bool Exists, string FolderPath, int? SeasonNumber,
    List<InventoryFile> Files);

public sealed record SearchPage(int Total, int Page, int PageSize, List<SearchHit> Items);

public sealed record CatalogLink(int Id, string Url, string Host, string? Quality, string? AudioLangs, string? SubLangs, long? SizeBytes,
    string Source = "catalog", int? HarvesterArticleId = null);
// BestLinkId is the result of running BestVariantPicker server-side
// against the user's settings (HosterPriority, QualityPreference,
// AudioPreference). It's null only when the title/episode has no links
// at all. The client should never re-rank — every "best variant"
// decision in the WASM UI reads this field.
public sealed record CatalogEpisode(int Id, int SeasonNumber, int EpisodeNumber, string? EpisodeName, bool IsFullSeason, List<CatalogLink> Links, int? BestLinkId = null);
public sealed record TitleDetail(
    int Id, string Title, string? OriginalTitle, string Category, string? Poster,
    int? Year, double? Rating, int? Runtime, string? Overview, List<string> Genres,
    string? OriginalLanguage, string? ImdbId, int? TmdbId,
    bool MetadataUncertain, List<CatalogLink> Links, List<CatalogEpisode> Episodes,
    int? BestLinkId = null);

public sealed record IngestionStatus(string State, string? Description, IngestProgress Progress,
    string? Error, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt);
public sealed record IngestProgress(long Read, long Titles, long Episodes, long Links, long Failed);

public sealed record CatalogSendResult(bool Ok, string? Error, List<string> TaskIds, int Submitted);

// ── Top-bar lookup ──────────────────────────────────────────────────────
public sealed record LookupLink(int LinkId, string? Quality, string? AudioLangs, string Host, long? SizeBytes);
public sealed record LookupHit(
    int TitleId, string Title, string Category, string? Poster, int? Year,
    int LinkCount, int EpisodeCount, LookupLink? BestLink);
public sealed record LookupResult(List<LookupHit> Results, int Total = 0);

// ── Following ────────────────────────────────────────────────────────────
public sealed record FollowingItem(
    int CatalogTitleId, string Title, string? Poster, string Category,
    string? TmdbStatus, DateTimeOffset? LastAirDate,
    DateTimeOffset? NextEpisodeAirDate, string? NextEpisodeCode,
    DateTimeOffset? LastGrabbedAt,
    List<string> GrabbedEpisodes, List<string> AvailableMissingEpisodes);

// ── Discovery ───────────────────────────────────────────────────────────
public sealed record DiscoverySource(string Source, int Rank, string Reason);
public sealed record DiscoveryHit(
    int TitleId, string Title, int? Year, string Category, string? Poster,
    int LinkCount, LookupLink BestLink, List<DiscoverySource> Sources, bool AlreadyGrabbed);
public sealed record DiscoveryPage(List<DiscoveryHit> Results);
public sealed record DiscoveryRefreshSummary(int Tmdb, int Trakt, int Letterboxd, int Plex, int Pruned);

public sealed record CatalogSearchQuery
{
    public string? Q { get; set; }
    public string? Category { get; set; }
    public List<string> Hosts { get; set; } = new();
    public List<string> Qualities { get; set; } = new();
    public List<string> AudioLangs { get; set; } = new();
    public List<string> Genres { get; set; } = new();
    public int? YearMin { get; set; }
    public int? YearMax { get; set; }
    public double? RatingMin { get; set; }
    public int? RuntimeMin { get; set; }
    public int? RuntimeMax { get; set; }
    public long? SizeMinBytes { get; set; }
    public long? SizeMaxBytes { get; set; }
    public string? OriginalLanguage { get; set; }
    public bool HasMetadataOnly { get; set; }
    public string Sort { get; set; } = "popularity";
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
}
