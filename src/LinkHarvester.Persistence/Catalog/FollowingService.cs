using LinkHarvester.Core;
using Microsoft.EntityFrameworkCore;

namespace LinkHarvester.Persistence.Catalog;

/// <summary>
/// Read-side query for the Following page. A title is implicitly Followed
/// when:
///   * It is a TV/anime category (has at least one CatalogEpisodeEntity), and
///   * At least one prior submission for the title reached Sent status, and
///   * The user hasn't dismissed it via FollowingDismissalEntity.
///
/// No time decay — a show grabbed 18 months ago still appears. This is the
/// whole point of the feature: shows that air rarely shouldn't drop off.
/// </summary>
public sealed class FollowingService
{
    private readonly IDbContextFactory<HarvesterDbContext> _factory;

    public FollowingService(IDbContextFactory<HarvesterDbContext> factory)
    {
        _factory = factory;
    }

    public async Task<List<FollowingItem>> GetAllAsync(CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);

        // Step 1 — distinct titles that have any Sent submission and have episodes.
        var followedTitleIds = await db.Submissions.AsNoTracking()
            .Where(s => s.Status == SubmissionStatus.Sent && s.CatalogTitleId != null)
            .Select(s => s.CatalogTitleId!.Value)
            .Distinct()
            .ToListAsync(ct);
        if (followedTitleIds.Count == 0) return new();

        var dismissed = await db.FollowingDismissals.AsNoTracking()
            .Where(d => followedTitleIds.Contains(d.CatalogTitleId))
            .Select(d => d.CatalogTitleId)
            .ToListAsync(ct);
        var dismissedSet = new HashSet<int>(dismissed);

        var titles = await db.CatalogTitles.AsNoTracking()
            .Where(t => followedTitleIds.Contains(t.Id) && !dismissedSet.Contains(t.Id))
            .Include(t => t.Metadata)
            .Include(t => t.Episodes)
            .Where(t => t.Episodes.Any())
            .ToListAsync(ct);
        if (titles.Count == 0) return new();

        var titleIds = titles.Select(t => t.Id).ToList();

        // Step 2 — per-title grabbed-episode set, derived from Submissions ⋈
        // article ⋈ catalog links. We treat any Sent submission that produced
        // a CatalogLinkEntity row with an EpisodeId as "grabbed".
        var sentArticleIds = await db.Submissions.AsNoTracking()
            .Where(s => titleIds.Contains(s.CatalogTitleId ?? 0)
                     && s.Status == SubmissionStatus.Sent
                     && s.ArticleId != null)
            .Select(s => new { s.CatalogTitleId, s.ArticleId, s.SubmittedAt })
            .ToListAsync(ct);

        var sentArticleIdSet = sentArticleIds.Select(x => x.ArticleId!.Value).ToHashSet();
        var sentLinks = sentArticleIdSet.Count == 0
            ? new List<CatalogLinkEntity>()
            : await db.CatalogLinks.AsNoTracking()
                .Where(l => l.HarvesterArticleId != null && sentArticleIdSet.Contains(l.HarvesterArticleId!.Value))
                .Include(l => l.Episode)
                .ToListAsync(ct);

        var grabbedByTitle = new Dictionary<int, HashSet<string>>();
        foreach (var l in sentLinks)
        {
            if (l.Episode is null) continue;
            var code = EpisodeCode(l.Episode);
            if (code is null) continue;
            if (!grabbedByTitle.TryGetValue(l.TitleId, out var set))
            {
                set = new(StringComparer.OrdinalIgnoreCase);
                grabbedByTitle[l.TitleId] = set;
            }
            set.Add(code);
        }

        // Step 3 — per-title last-grab timestamp (from Submissions).
        var lastGrabByTitle = sentArticleIds
            .GroupBy(x => x.CatalogTitleId!.Value)
            .ToDictionary(g => g.Key, g => g.Max(x => x.SubmittedAt));

        // Step 4 — pending detections (unconsumed) — count per title for the badge.
        var pendingDetections = await db.FollowingDetectionLog.AsNoTracking()
            .Where(d => titleIds.Contains(d.CatalogTitleId) && d.ConsumedAt == null)
            .GroupBy(d => d.CatalogTitleId)
            .Select(g => new { TitleId = g.Key, Codes = g.Select(x => x.EpisodeCode).ToList() })
            .ToListAsync(ct);
        var pendingByTitle = pendingDetections.ToDictionary(p => p.TitleId, p => p.Codes);

        // Step 5 — compose.
        return titles.Select(t =>
        {
            var grabbed = grabbedByTitle.TryGetValue(t.Id, out var g) ? g : new HashSet<string>();
            var grabbedList = grabbed.OrderBy(c => c).ToList();
            var pending = pendingByTitle.TryGetValue(t.Id, out var p) ? p : new List<string>();
            var availableMissing = pending.Where(c => !grabbed.Contains(c)).ToList();
            var lastGrab = lastGrabByTitle.TryGetValue(t.Id, out var ts) ? ts : (DateTimeOffset?)null;

            return new FollowingItem(
                CatalogTitleId: t.Id,
                Title: t.TitleName,
                Poster: t.TitlePoster,
                Category: t.CategoryName,
                TmdbStatus: t.Metadata?.Status,
                LastAirDate: t.Metadata?.LastAirDate,
                NextEpisodeAirDate: t.Metadata?.NextEpisodeAirDate,
                NextEpisodeCode: t.Metadata?.NextEpisodeCode,
                LastGrabbedAt: lastGrab,
                GrabbedEpisodes: grabbedList,
                AvailableMissingEpisodes: availableMissing);
        })
        // Surface shows with missing-available-now first; then by recency.
        .OrderByDescending(x => x.AvailableMissingEpisodes.Count > 0)
        .ThenByDescending(x => x.LastGrabbedAt ?? DateTimeOffset.MinValue)
        .ToList();
    }

    public async Task DismissAsync(int catalogTitleId, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        if (await db.FollowingDismissals.AnyAsync(d => d.CatalogTitleId == catalogTitleId, ct))
            return;
        db.FollowingDismissals.Add(new FollowingDismissalEntity
        {
            CatalogTitleId = catalogTitleId,
            DismissedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task UndismissAsync(int catalogTitleId, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var row = await db.FollowingDismissals.FirstOrDefaultAsync(d => d.CatalogTitleId == catalogTitleId, ct);
        if (row is null) return;
        db.FollowingDismissals.Remove(row);
        await db.SaveChangesAsync(ct);
    }

    private static string? EpisodeCode(CatalogEpisodeEntity ep)
    {
        if (ep.IsFullSeason) return $"S{ep.SeasonNumber:D2}";
        if (ep.EpisodeNumber <= 0) return null;
        return $"S{ep.SeasonNumber:D2}E{ep.EpisodeNumber:D2}";
    }
}

public sealed record FollowingItem(
    int CatalogTitleId,
    string Title,
    string? Poster,
    string Category,
    string? TmdbStatus,
    DateTimeOffset? LastAirDate,
    DateTimeOffset? NextEpisodeAirDate,
    string? NextEpisodeCode,
    DateTimeOffset? LastGrabbedAt,
    List<string> GrabbedEpisodes,
    List<string> AvailableMissingEpisodes);
