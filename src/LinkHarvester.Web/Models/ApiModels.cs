namespace LinkHarvester.Web.Models;

public sealed record InboxCard(
    int TitleId,
    string DisplayTitle,
    int? Year,
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
    string SynologyBaseUrl, string SynologyUsername, bool SynologyPasswordSet,
    string? SynologyOtpCode, string SynologyMovieDestination, string SynologySeriesDestination,
    int ScanIntervalMinutes, bool ScanOnStartup, List<string> HosterPriority,
    string AuthUsername, bool AuthPasswordSet);

public sealed record UpdateSettings(
    string? SynologyBaseUrl, string? SynologyUsername, string? SynologyPassword,
    string? SynologyOtpCode, string? SynologyMovieDestination, string? SynologySeriesDestination,
    int? ScanIntervalMinutes, bool? ScanOnStartup, List<string>? HosterPriority,
    string? AuthUsername, string? AuthPassword);

public sealed record SynologyTestResult(bool Ok, string? Error);
