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
/// Feed source that exposes a paginated backlog suitable for backfill runs.
/// Implementations walk historical category pages and yield items as they
/// are discovered, stopping when the page's oldest item is older than
/// <c>since</c>.
/// </summary>
public interface IBackfillFeedSource : IFeedSource
{
    /// <summary>
    /// Walk the historical listing for <paramref name="kind"/> (e.g.
    /// "films", "series", "mangas") starting at <paramref name="startPage"/>
    /// and yield each page's items in order, oldest article last.
    /// Pages keep being fetched as long as the listing returns items AND
    /// the page contains at least one item published on or after <paramref name="since"/>.
    /// </summary>
    IAsyncEnumerable<BackfillListingPage> ListSinceAsync(
        string kind,
        DateTimeOffset since,
        int startPage,
        CancellationToken ct);
}

/// <summary>
/// One page of items yielded by <see cref="IBackfillFeedSource.ListSinceAsync"/>.
/// </summary>
public sealed record BackfillListingPage(
    int Page,
    string Kind,
    IReadOnlyList<RawListItem> Items,
    DateTimeOffset? OldestPublishedAt,
    DateTimeOffset? NewestPublishedAt);

/// <summary>
/// Resolves a dl-protect-style protected URL into one or more final hoster links.
/// </summary>
public interface ILinkResolver
{
    Task<ResolutionOutcome> ResolveAsync(string protectedUrl, CancellationToken ct);
}

/// <summary>
/// Synology DownloadStation submission API. Also exposes the minimum
/// FileStation surface needed to mkdir destination folders before submitting
/// a download — DSM auto-creation is unreliable on nested paths, so we always
/// create the folder up front.
/// </summary>
public interface IDownloadStationClient
{
    Task<IReadOnlyList<string>> CreateTasksAsync(IEnumerable<string> urls, string? destination, CancellationToken ct);

    /// <summary>
    /// Ensure each folder path in <paramref name="folderPaths"/> exists on the
    /// NAS volume, creating intermediate parents as needed. Paths are
    /// relative to the volume root (no leading slash). Idempotent — existing
    /// folders are left alone.
    /// </summary>
    Task EnsureFoldersAsync(IEnumerable<string> folderPaths, CancellationToken ct);
}

public interface IQualityScorer
{
    QualityRating Score(string? qualityLabel, string? language);
}

public interface ITitleNormalizer
{
    string Normalize(string raw);
}
