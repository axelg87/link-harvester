using System.Text.Json;
using LinkHarvester.Core;
using LinkHarvester.Persistence.Cards;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LinkHarvester.Persistence.Catalog;

public sealed class HarvesterCatalogPromoter
{
    private readonly IDbContextFactory<HarvesterDbContext> _factory;
    private readonly ICardKeeper _cards;
    private readonly ILogger<HarvesterCatalogPromoter> _log;

    public HarvesterCatalogPromoter(
        IDbContextFactory<HarvesterDbContext> factory,
        ICardKeeper cards,
        ILogger<HarvesterCatalogPromoter> log)
    {
        _factory = factory;
        _cards = cards;
        _log = log;
    }

    public async Task<int> PromoteArticleAsync(int articleId, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var article = await db.Articles
            .Include(a => a.Title)
            .FirstOrDefaultAsync(a => a.Id == articleId, ct)
            ?? throw new InvalidOperationException($"No article {articleId}");

        var title = article.Title;
        var canonical = $"harvester:{title.Canonical}";
        var catalogTitle = title.CatalogTitleId is int existingId
            ? await db.CatalogTitles.FirstOrDefaultAsync(t => t.Id == existingId, ct)
            : null;
        catalogTitle ??= await db.CatalogTitles.FirstOrDefaultAsync(t => t.CanonicalKey == canonical, ct);

        if (catalogTitle is null)
        {
            catalogTitle = new CatalogTitleEntity
            {
                CanonicalKey = canonical,
                FirstSeenAt = article.DiscoveredAt,
                LinkCount = 0
            };
            db.CatalogTitles.Add(catalogTitle);
        }

        catalogTitle.TitleName = title.DisplayTitle;
        catalogTitle.NormalizedTitle = title.NormalizedTitle;
        catalogTitle.ImdbId = title.ImdbId;
        catalogTitle.TmdbId = title.TmdbId;
        catalogTitle.CategoryName = MapCategory(title.Kind);
        catalogTitle.LastSeenAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);

        var existingLinks = await db.CatalogLinks
            .Where(l => l.HarvesterArticleId == article.Id)
            .ToListAsync(ct);
        if (existingLinks.Count > 0)
        {
            db.CatalogLinks.RemoveRange(existingLinks);
            await db.SaveChangesAsync(ct);
        }

        var hosters = ParseHosters(article.HostersJson);
        var linkOrdinal = 0;
        foreach (var hoster in hosters)
        {
            foreach (var protectedLink in hoster.Links)
            {
                linkOrdinal++;
                var episodeId = await ResolveEpisodeIdAsync(db, catalogTitle.Id, title, protectedLink.EpisodeIndex, ct);
                db.CatalogLinks.Add(new CatalogLinkEntity
                {
                    TitleId = catalogTitle.Id,
                    EpisodeId = episodeId,
                    ExternalLinkId = BuildExternalLinkId(article.Id, linkOrdinal),
                    LinkSource = "zt",
                    HarvesterArticleId = article.Id,
                    LinkUrl = protectedLink.Url,
                    HostName = hoster.Hoster,
                    NormalizedHost = hoster.Hoster.ToLowerInvariant(),
                    QualityName = article.QualityLabel,
                    AudioLangs = article.Language,
                    SizeBytes = article.SizeBytes,
                    CreatedAt = article.DiscoveredAt
                });
            }
        }

        if (linkOrdinal == 0)
        {
            db.CatalogLinks.Add(new CatalogLinkEntity
            {
                TitleId = catalogTitle.Id,
                EpisodeId = await ResolveEpisodeIdAsync(db, catalogTitle.Id, title, null, ct),
                ExternalLinkId = BuildExternalLinkId(article.Id, 1),
                LinkSource = "zt",
                HarvesterArticleId = article.Id,
                LinkUrl = article.Url,
                HostName = "ZT",
                NormalizedHost = "zt",
                QualityName = article.QualityLabel,
                AudioLangs = article.Language,
                SizeBytes = article.SizeBytes,
                CreatedAt = article.DiscoveredAt
            });
        }

        var meta = await db.CatalogTitleMetadata.FirstOrDefaultAsync(m => m.TitleId == catalogTitle.Id, ct);
        if (meta is null)
        {
            db.CatalogTitleMetadata.Add(new CatalogTitleMetadataEntity
            {
                TitleId = catalogTitle.Id,
                TmdbId = title.TmdbId,
                ImdbId = title.ImdbId,
                Year = title.Year,
                EnrichmentSource = "pending"
            });
        }
        else
        {
            meta.TmdbId ??= title.TmdbId;
            meta.ImdbId ??= title.ImdbId;
            meta.Year ??= title.Year;
        }

        title.CatalogTitleId = catalogTitle.Id;
        title.CatalogPromotedAt = DateTimeOffset.UtcNow;
        catalogTitle.LinkCount = await db.CatalogLinks.CountAsync(l => l.TitleId == catalogTitle.Id, ct);
        catalogTitle.EpisodeCount = await db.CatalogEpisodes.CountAsync(e => e.TitleId == catalogTitle.Id, ct);

        await db.SaveChangesAsync(ct);

        // Refresh both card surfaces. Promotion changes the catalog title's
        // link counts and creates/updates Catalog/Genre/LinkFacet rows; it
        // also flips the Harvester title's CatalogTitleId, which the inbox
        // card carries so the inbox poster lookup stays accurate.
        await _cards.UpsertCatalogCardAsync(db, catalogTitle.Id, ct);
        await _cards.UpsertInboxCardAsync(db, title.Id, ct);

        _log.LogInformation("Promoted ZT article {ArticleId} to catalog title {CatalogTitleId}.", articleId, catalogTitle.Id);
        return catalogTitle.Id;
    }

    public async Task<int> BackfillUnpromotedAsync(CancellationToken ct)
    {
        var total = 0;
        List<int> articleIds;
        await using (var db = await _factory.CreateDbContextAsync(ct))
        {
            articleIds = await db.Articles
                .AsNoTracking()
                .Where(a => a.Status == ArticleStatus.Parsed
                    || a.Status == ArticleStatus.Resolved
                    || a.Status == ArticleStatus.InInbox
                    || a.Status == ArticleStatus.Submitted)
                .OrderByDescending(a => a.DiscoveredAt)
                .Select(a => a.Id)
                .ToListAsync(ct);
        }

        foreach (var articleId in articleIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await PromoteArticleAsync(articleId, ct);
                total++;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Could not backfill article {ArticleId} into catalog.", articleId);
            }
        }
        return total;
    }

    private static string MapCategory(TitleKind kind) => kind switch
    {
        TitleKind.Movie => "Films",
        TitleKind.Anime => "Animes",
        TitleKind.Season => "Séries",
        _ => "Other"
    };

    private static long BuildExternalLinkId(int articleId, int ordinal) =>
        -(((long)articleId * 10_000L) + ordinal);

    private static List<HosterLinks> ParseHosters(string json)
    {
        try { return JsonSerializer.Deserialize<List<HosterLinks>>(json) ?? new(); }
        catch { return new(); }
    }

    private static async Task<int?> ResolveEpisodeIdAsync(
        HarvesterDbContext db,
        int catalogTitleId,
        TitleEntity title,
        int? episodeIndex,
        CancellationToken ct)
    {
        if (title.Kind is not (TitleKind.Season or TitleKind.Anime)) return null;

        var season = title.SeasonNumber ?? 0;
        var episodeNumber = episodeIndex ?? 0;
        var isFullSeason = episodeIndex is null;
        var episode = await db.CatalogEpisodes.FirstOrDefaultAsync(e =>
            e.TitleId == catalogTitleId
            && e.SeasonNumber == season
            && e.EpisodeNumber == episodeNumber
            && e.IsFullSeason == isFullSeason, ct);
        if (episode is not null) return episode.Id;

        episode = new CatalogEpisodeEntity
        {
            TitleId = catalogTitleId,
            SeasonNumber = season,
            EpisodeNumber = episodeNumber,
            IsFullSeason = isFullSeason,
            EpisodeName = isFullSeason
                ? season > 0 ? $"Season {season}" : "Season"
                : $"Episode {episodeNumber}"
        };
        db.CatalogEpisodes.Add(episode);
        await db.SaveChangesAsync(ct);
        return episode.Id;
    }

}
