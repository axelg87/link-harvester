using LinkHarvester.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LinkHarvester.Persistence.Catalog;

/// <summary>
/// Pure-DB check fired at the tail of every article ingest. If the
/// newly-promoted article belongs to a series the user has previously
/// submitted ("implicitly Following"), and it brings an episode that has
/// not yet been submitted, this appends a <see cref="FollowingDetectionLogEntity"/>
/// row for downstream notification consumers (Telegram bot, etc.).
///
/// Idempotent — the unique index on (CatalogTitleId, EpisodeCode) means
/// a re-ingest during backfill cannot produce a second log row.
/// </summary>
public sealed class FollowingDetectionService
{
    private readonly IDbContextFactory<HarvesterDbContext> _factory;
    private readonly ILogger<FollowingDetectionService> _log;

    public FollowingDetectionService(
        IDbContextFactory<HarvesterDbContext> factory,
        ILogger<FollowingDetectionService> log)
    {
        _factory = factory;
        _log = log;
    }

    /// <summary>
    /// Called by ScanPipeline after PromoteArticleAsync. Walks the article's
    /// catalog links and writes detections for any (title, episode-code) the
    /// user is following but has never submitted.
    /// </summary>
    public async Task RegisterIngestedAsync(int harvesterArticleId, CancellationToken ct)
    {
        try
        {
            await using var db = await _factory.CreateDbContextAsync(ct);

            var links = await db.CatalogLinks.AsNoTracking()
                .Where(l => l.HarvesterArticleId == harvesterArticleId)
                .Include(l => l.Episode)
                .ToListAsync(ct);
            if (links.Count == 0) return;

            var titleId = links[0].TitleId;

            // Was the user ever Following? — any prior Sent submission for this title.
            // Excludes the submission flowing through right now (the just-ingested
            // article hasn't been sent yet by definition).
            var hasPriorSubmission = await db.Submissions
                .AnyAsync(s => s.CatalogTitleId == titleId && s.Status == SubmissionStatus.Sent, ct);
            if (!hasPriorSubmission) return;

            // Skip if dismissed.
            if (await db.FollowingDismissals.AnyAsync(d => d.CatalogTitleId == titleId, ct))
                return;

            // Compute the set of episode codes already covered by prior Sent
            // submissions for this title, so we don't notify on a re-ingest of
            // an episode the user has already grabbed.
            var sentArticleIds = await db.Submissions
                .Where(s => s.CatalogTitleId == titleId && s.Status == SubmissionStatus.Sent && s.ArticleId != null)
                .Select(s => s.ArticleId!.Value)
                .ToListAsync(ct);
            HashSet<string> sentEpisodeCodes = new(StringComparer.OrdinalIgnoreCase);
            if (sentArticleIds.Count > 0)
            {
                var sentLinks = await db.CatalogLinks.AsNoTracking()
                    .Where(l => l.HarvesterArticleId != null && sentArticleIds.Contains(l.HarvesterArticleId!.Value))
                    .Include(l => l.Episode)
                    .ToListAsync(ct);
                foreach (var sl in sentLinks)
                {
                    var code = EpisodeCodeOf(sl);
                    if (code is not null) sentEpisodeCodes.Add(code);
                }
            }

            HashSet<string> emitted = new(StringComparer.OrdinalIgnoreCase);
            foreach (var l in links)
            {
                var code = EpisodeCodeOf(l);
                if (code is null) continue;
                if (sentEpisodeCodes.Contains(code)) continue;
                if (!emitted.Add(code)) continue;

                // Unique index guards against duplicates from backfill re-ingest.
                try
                {
                    db.FollowingDetectionLog.Add(new FollowingDetectionLogEntity
                    {
                        CatalogTitleId = titleId,
                        EpisodeCode = code,
                        ArticleId = harvesterArticleId,
                        CatalogLinkId = l.Id,
                        DetectedAt = DateTimeOffset.UtcNow
                    });
                    await db.SaveChangesAsync(ct);
                }
                catch (DbUpdateException)
                {
                    // Already logged — fine.
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "following detection failed for article {ArticleId}", harvesterArticleId);
        }
    }

    private static string? EpisodeCodeOf(CatalogLinkEntity link)
    {
        var ep = link.Episode;
        if (ep is null) return null;
        if (ep.IsFullSeason) return $"S{ep.SeasonNumber:D2}";
        if (ep.EpisodeNumber <= 0) return null;
        return $"S{ep.SeasonNumber:D2}E{ep.EpisodeNumber:D2}";
    }
}
