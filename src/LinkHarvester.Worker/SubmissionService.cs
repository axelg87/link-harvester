using System.Text.Json;
using LinkHarvester.Core;
using LinkHarvester.Persistence;
using LinkHarvester.Persistence.Catalog;
using LinkHarvester.Synology;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LinkHarvester.Worker;

/// <summary>
/// Coordinates resolving + sending an article to DownloadStation.
/// Resolution is on-demand (lazy) so we don't burn captcha budget on items
/// the user never picks.
/// </summary>
public sealed class SubmissionService
{
    /// <summary>
    /// Ignore repeat <see cref="SendToDsmAsync"/> calls for the same article
    /// within this window. Catches mobile double-tap and user-retry-after-timeout,
    /// the two failure modes that caused real-world double-downloads.
    /// </summary>
    public static readonly TimeSpan DuplicateSendWindow = TimeSpan.FromMinutes(2);

    private readonly IDbContextFactory<HarvesterDbContext> _factory;
    private readonly ILinkResolver _resolver;
    private readonly IDownloadStationClient _dsm;
    private readonly ISettingsService _settings;
    private readonly ILogger<SubmissionService> _log;

    public SubmissionService(IDbContextFactory<HarvesterDbContext> factory,
                              ILinkResolver resolver,
                              IDownloadStationClient dsm,
                              ISettingsService settings,
                              ILogger<SubmissionService> log)
    {
        _factory = factory;
        _resolver = resolver;
        _dsm = dsm;
        _settings = settings;
        _log = log;
    }

    /// <summary>
    /// Resolves dl-protect links for an article, in priority order, until at least
    /// one hoster is fully resolved. Persists the resolution rows.
    /// </summary>
    public async Task<IReadOnlyList<ResolvedLinkEntity>> ResolveArticleAsync(int articleId, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var article = await db.Articles
            .Include(a => a.ResolvedLinks)
            .FirstOrDefaultAsync(a => a.Id == articleId, ct)
            ?? throw new InvalidOperationException($"No article {articleId}");

        if (article.ResolvedLinks.Count > 0)
            return article.ResolvedLinks.ToList();

        var hosters = JsonSerializer.Deserialize<List<HosterLinks>>(article.HostersJson) ?? new();
        var ordered = OrderByPriority(hosters);

        ResolutionAttemptResult lastResult = ResolutionAttemptResult.Unknown;
        foreach (var hoster in ordered)
        {
            var resolvedForHoster = new List<ResolvedLinkEntity>();
            bool failed = false;
            foreach (var link in hoster.Links)
            {
                var outcome = await _resolver.ResolveAsync(link.Url, ct);
                lastResult = outcome.Result;
                if (outcome.Result != ResolutionAttemptResult.Success || outcome.Links.Count == 0)
                {
                    _log.LogWarning("Hoster {H} link {U} did not resolve ({R})", hoster.Hoster, link.Url, outcome.Result);
                    failed = true;
                    break;
                }

                foreach (var rl in outcome.Links)
                {
                    resolvedForHoster.Add(new ResolvedLinkEntity
                    {
                        ArticleId = article.Id,
                        Hoster = string.IsNullOrEmpty(rl.Hoster) ? hoster.Hoster : rl.Hoster,
                        Url = rl.Url,
                        EpisodeIndex = link.EpisodeIndex,
                        ResolvedAt = DateTimeOffset.UtcNow
                    });
                }
            }

            if (!failed && resolvedForHoster.Count > 0)
            {
                db.ResolvedLinks.AddRange(resolvedForHoster);
                article.Status = ArticleStatus.Resolved;
                article.ResolvedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
                return resolvedForHoster;
            }

            article.ResolutionAttempts++;
        }

        article.Status = ArticleStatus.Failed;
        article.FailureReason = $"Could not resolve any hoster ({lastResult})";
        await db.SaveChangesAsync(ct);
        return Array.Empty<ResolvedLinkEntity>();
    }

    public async Task<SubmissionEntity> SendToDsmAsync(int articleId, CancellationToken ct)
    {
        // Idempotency gate: if this article was just submitted (Sent or
        // Pending) within the dedupe window, return that row instead of
        // creating a second one. Defends against mobile double-tap and
        // user-retry-after-timeout, both observed in production.
        var since = DateTimeOffset.UtcNow - DuplicateSendWindow;
        await using (var lookupDb = await _factory.CreateDbContextAsync(ct))
        {
            var recent = await lookupDb.Submissions
                .Where(s => s.ArticleId == articleId
                            && s.SubmittedAt >= since
                            && (s.Status == SubmissionStatus.Sent || s.Status == SubmissionStatus.Pending))
                .OrderByDescending(s => s.Id)
                .FirstOrDefaultAsync(ct);
            if (recent is not null)
            {
                _log.LogInformation(
                    "Skipping duplicate send for article {Id}: existing submission {Sub} already {Status}.",
                    articleId, recent.Id, recent.Status);
                return recent;
            }
        }

        var resolved = await ResolveArticleAsync(articleId, ct);
        if (resolved.Count == 0)
        {
            await using var failDb = await _factory.CreateDbContextAsync(ct);
            var failedArticle = await failDb.Articles.Include(a => a.Title).FirstAsync(a => a.Id == articleId, ct);
            var failed = new SubmissionEntity
            {
                ArticleId = articleId,
                Source = SendSourceKind.ZtArticle,
                CatalogTitleId = failedArticle.Title.CatalogTitleId,
                DisplayTitle = failedArticle.Title.DisplayTitle,
                Status = SubmissionStatus.ResolutionFailed,
                ResponseMessage = failedArticle.FailureReason ?? "No resolved links to send.",
                SubmittedAt = DateTimeOffset.UtcNow,
                CompletedAt = DateTimeOffset.UtcNow
            };
            failDb.Submissions.Add(failed);
            await failDb.SaveChangesAsync(ct);
            return failed;
        }

        await using var db = await _factory.CreateDbContextAsync(ct);
        var article = await db.Articles
            .Include(a => a.Title)
            .FirstAsync(a => a.Id == articleId, ct);

        // Compute the kind-aware destination. Series and Anime get a
        // <Root>/<Title>/Season N layout so episodes don't dump flat into
        // /video/series and mix across shows.
        var plan = BuildDestination(article.Title, _settings.Current);
        var dest = plan.FullPath;

        // Make sure every intermediate folder exists before submitting. DSM's
        // own auto-mkdir is unreliable for nested paths.
        if (plan.ParentSegments.Count > 0)
        {
            try
            {
                await _dsm.EnsureFoldersAsync(plan.ParentSegments, ct);
            }
            catch (DsmException ex)
            {
                _log.LogWarning(ex, "FileStation.CreateFolder failed for article {Id} dest {Dest}", articleId, dest);
                var folderFailed = new SubmissionEntity
                {
                    ArticleId = articleId,
                    Source = SendSourceKind.ZtArticle,
                    CatalogTitleId = article.Title.CatalogTitleId,
                    DisplayTitle = article.Title.DisplayTitle,
                    Destination = dest,
                    UrlCount = 0,
                    SubmittedUrlsJson = "[]",
                    Status = SubmissionStatus.Failed,
                    ResponseMessage = $"Could not create destination folder: {ex.HumanMessage}",
                    DsmErrorCode = ex.Code,
                    SubmittedAt = DateTimeOffset.UtcNow,
                    CompletedAt = DateTimeOffset.UtcNow
                };
                db.Submissions.Add(folderFailed);
                await db.SaveChangesAsync(ct);
                return folderFailed;
            }
        }

        var urls = resolved.Select(r => r.Url).ToList();
        var submission = new SubmissionEntity
        {
            ArticleId = articleId,
            Source = SendSourceKind.ZtArticle,
            CatalogTitleId = article.Title.CatalogTitleId,
            DisplayTitle = article.Title.DisplayTitle,
            Destination = dest,
            UrlCount = urls.Count,
            SubmittedUrlsJson = JsonSerializer.Serialize(urls),
            Status = SubmissionStatus.Pending,
            SubmittedAt = DateTimeOffset.UtcNow
        };
        db.Submissions.Add(submission);
        await db.SaveChangesAsync(ct);

        try
        {
            var taskIds = await _dsm.CreateTasksAsync(urls, dest, ct);
            submission.Status = SubmissionStatus.Sent;
            submission.DsmTaskIdsJson = JsonSerializer.Serialize(taskIds);
            submission.CompletedAt = DateTimeOffset.UtcNow;
            article.Status = ArticleStatus.Submitted;
            article.SubmittedAt = DateTimeOffset.UtcNow;

            article.Title.Status = TitleStatus.Submitted;
            if (article.Title.BestSubmittedQualityScore is null ||
                article.QualityScore > article.Title.BestSubmittedQualityScore)
            {
                article.Title.BestSubmittedQualityScore = article.QualityScore;
            }
            article.Title.UpdatedAt = DateTimeOffset.UtcNow;
        }
        catch (DsmException dx)
        {
            // Persist the user-facing message rather than the raw inner
            // exception so the inbox UI shows something actionable.
            submission.Status = SubmissionStatus.Failed;
            submission.ResponseMessage = dx.HumanMessage;
            submission.DsmErrorCode = dx.Code;
            submission.DsmFailedUrl = dx.FailedUrl;
            submission.CompletedAt = DateTimeOffset.UtcNow;
            _log.LogWarning(dx, "DSM rejected submission for article {Id} (code {Code})", articleId, dx.Code);
        }
        catch (Exception ex)
        {
            // Unexpected — keep the raw message but still mark failed.
            submission.Status = SubmissionStatus.Failed;
            submission.ResponseMessage = ex.Message;
            submission.CompletedAt = DateTimeOffset.UtcNow;
            _log.LogError(ex, "Unexpected error pushing to DSM for article {Id}", articleId);
        }

        await db.SaveChangesAsync(ct);
        return submission;
    }

    public async Task SkipArticleAsync(int articleId, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var article = await db.Articles
            .Include(a => a.Title)
            .FirstAsync(a => a.Id == articleId, ct);
        article.Status = ArticleStatus.Skipped;

        // If the title has no other inbox-grade articles, mark it skipped too.
        var anyOpen = await db.Articles.AnyAsync(a =>
            a.TitleId == article.TitleId &&
            a.Id != article.Id &&
            (a.Status == ArticleStatus.Parsed || a.Status == ArticleStatus.Resolved || a.Status == ArticleStatus.InInbox), ct);
        if (!anyOpen && article.Title.Status != TitleStatus.Submitted)
        {
            article.Title.Status = TitleStatus.Skipped;
        }

        await db.SaveChangesAsync(ct);
    }

    private List<HosterLinks> OrderByPriority(List<HosterLinks> hosters)
    {
        var priority = _settings.Current.HosterPriority;
        return hosters
            .OrderBy(h =>
            {
                int idx = -1;
                for (int i = 0; i < priority.Count; i++)
                {
                    if (string.Equals(priority[i], h.Hoster, StringComparison.OrdinalIgnoreCase))
                    { idx = i; break; }
                }
                return idx < 0 ? int.MaxValue : idx;
            })
            .Where(h =>
            {
                if (priority.Count == 0) return true;
                return priority.Any(p => string.Equals(p, h.Hoster, StringComparison.OrdinalIgnoreCase));
            })
            .ToList();
    }

    private static DsmDestinationBuilder.DestinationPlan BuildDestination(
        TitleEntity title,
        AppSettingsSnapshot s)
    {
        return title.Kind switch
        {
            TitleKind.Movie => DsmDestinationBuilder.ForMovie(s.SynologyMovieDestination),
            TitleKind.Anime => DsmDestinationBuilder.ForAnime(
                s.SynologyAnimeDestination, title.DisplayTitle, title.SeasonNumber),
            TitleKind.Season => DsmDestinationBuilder.ForSeries(
                s.SynologySeriesDestination, title.DisplayTitle, title.SeasonNumber),
            _ => DsmDestinationBuilder.ForMovie(s.SynologyMovieDestination),
        };
    }
}
