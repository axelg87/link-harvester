namespace LinkHarvester.Core;

/// <summary>
/// Stable identifier for a piece of content as understood by humans, regardless
/// of which article/source/quality it came from.
/// Movies: (NormalizedTitle, Year, Movie, null).
/// Seasons: (NormalizedTitle, Year?, Season, SeasonNumber).
/// </summary>
public sealed record TitleKey(string NormalizedTitle, int? Year, TitleKind Kind, int? SeasonNumber)
{
    public string ToCanonical() => $"{Kind}|{NormalizedTitle}|{Year?.ToString() ?? "_"}|{SeasonNumber?.ToString() ?? "_"}";
}

/// <summary>
/// Lightweight item produced when listing a feed; a pointer to an article we may parse later.
/// </summary>
public sealed record RawListItem(
    string SourceId,
    string ExternalId,
    string Url,
    string? Title,
    string? QualityHint,
    string? LanguageHint,
    DateTimeOffset? PublishedAt);

/// <summary>
/// One protected (dl-protect) link, optionally tagged with an episode index for series.
/// </summary>
public sealed record ProtectedLink(string Url, int? EpisodeIndex);

/// <summary>
/// All protected links offered by a single hoster on an article (1 entry for movies, N for series).
/// </summary>
public sealed record HosterLinks(string Hoster, IReadOnlyList<ProtectedLink> Links);

/// <summary>
/// A fully parsed article, ready to be normalized into Title + Article + variant siblings.
/// </summary>
public sealed record ArticleDetails(
    string SourceId,
    string ExternalId,
    string Url,
    string Title,
    int? Year,
    int? SeasonNumber,
    TitleKind Kind,
    string? QualityLabel,
    string? Language,
    long? SizeBytes,
    int? EpisodeCount,
    string AggregatorDlProtectUrl,
    IReadOnlyList<HosterLinks> Hosters,
    IReadOnlyList<string> SiblingArticleUrls,
    string ContentHash);

/// <summary>
/// Output of a quality string parser.
/// </summary>
public sealed record QualityRating(int Score, string Resolution, string? Codec, string? Source, string? Language);

/// <summary>
/// One final downloadable URL extracted from dl-protect for an article.
/// </summary>
public sealed record ResolvedLink(string Hoster, string Url, int? EpisodeIndex);

/// <summary>
/// What dl-protect resolved into.
/// </summary>
public sealed record ResolutionOutcome(
    ResolutionAttemptResult Result,
    IReadOnlyList<ResolvedLink> Links,
    string? ErrorMessage,
    int CapSolverCalls,
    decimal CapSolverCostUsd);
