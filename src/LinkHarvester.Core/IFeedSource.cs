namespace LinkHarvester.Core;

/// <summary>
/// A pluggable provider. Implementations must be stateless apart from
/// any HttpClient/Browser they depend on; per-run state lives in the DB.
/// </summary>
public interface IFeedSource
{
    string Id { get; }
    string DisplayName { get; }

    /// <summary>
    /// Discover candidate articles from the source's "what's new" surface.
    /// MUST be cheap and idempotent; does not parse article bodies.
    /// </summary>
    Task<IReadOnlyList<RawListItem>> ListNewAsync(CancellationToken ct);

    /// <summary>
    /// Fetch and parse a single article into a fully-formed details record.
    /// </summary>
    Task<ArticleDetails> FetchArticleAsync(string articleUrl, CancellationToken ct);

    /// <summary>
    /// Build the title-level identity for an article, used to dedup variants.
    /// </summary>
    TitleKey BuildTitleKey(ArticleDetails article);
}

/// <summary>
/// Resolves a dl-protect-style protected URL into one or more final hoster links.
/// </summary>
public interface ILinkResolver
{
    Task<ResolutionOutcome> ResolveAsync(string protectedUrl, CancellationToken ct);
}

/// <summary>
/// Synology DownloadStation submission API.
/// </summary>
public interface IDownloadStationClient
{
    Task<IReadOnlyList<string>> CreateTasksAsync(IEnumerable<string> urls, string? destination, CancellationToken ct);
}

public interface IQualityScorer
{
    QualityRating Score(string? qualityLabel, string? language);
}

public interface ITitleNormalizer
{
    string Normalize(string raw);
}
