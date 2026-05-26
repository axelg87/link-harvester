using System.Text.Json;
using LinkHarvester.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LinkHarvester.Persistence.Catalog;

public sealed class HarvesterCatalogPromoter
{
    private readonly IDbContextFactory<HarvesterDbContext> _factory;
    private readonly ILogger<HarvesterCatalogPromoter> _log;

    public HarvesterCatalogPromoter(
        IDbContextFactory<HarvesterDbContext> factory,
        ILogger<HarvesterCatalogPromoter> log)
    {
        _factory = factory;
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

        int? episodeId = null;
        if (title.Kind is TitleKind.Season or TitleKind.Anime)
        {
            var season = title.SeasonNumber ?? 0;
            var episode = await db.CatalogEpisodes.FirstOrDefaultAsync(e =>
                e.TitleId == catalogTitle.Id
                && e.SeasonNumber == season
                && e.EpisodeNumber == 0
                && e.IsFullSeason, ct);
            if (episode is null)
            {
                episode = new CatalogEpisodeEntity
                {
                    TitleId = catalogTitle.Id,
                    SeasonNumber = season,
                    EpisodeNumber = 0,
                    IsFullSeason = true,
                    EpisodeName = season > 0 ? $"Season {season}" : "Season"
                };
                db.CatalogEpisodes.Add(episode);
                await db.SaveChangesAsync(ct);
            }
            episodeId = episode.Id;
        }

        var externalLinkId = -article.Id;
        var link = await db.CatalogLinks.FirstOrDefaultAsync(l => l.ExternalLinkId == externalLinkId, ct);
        if (link is null)
        {
            link = new CatalogLinkEntity
            {
                ExternalLinkId = externalLinkId,
                CreatedAt = article.DiscoveredAt,
                LinkSource = "zt",
                HarvesterArticleId = article.Id
            };
            db.CatalogLinks.Add(link);
        }

        link.TitleId = catalogTitle.Id;
        link.EpisodeId = episodeId;
        link.LinkSource = "zt";
        link.HarvesterArticleId = article.Id;
        link.LinkUrl = article.Url;
        link.HostName = SummarizeHosters(article.HostersJson);
        link.NormalizedHost = "zt";
        link.QualityName = article.QualityLabel;
        link.AudioLangs = article.Language;
        link.SizeBytes = article.SizeBytes;

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
        _log.LogInformation("Promoted ZT article {ArticleId} to catalog title {CatalogTitleId}.", articleId, catalogTitle.Id);
        return catalogTitle.Id;
    }

    private static string MapCategory(TitleKind kind) => kind switch
    {
        TitleKind.Movie => "Films",
        TitleKind.Anime => "Animes",
        TitleKind.Season => "Séries",
        _ => "Other"
    };

    private static string SummarizeHosters(string json)
    {
        try
        {
            var hosters = JsonSerializer.Deserialize<List<HosterLinks>>(json) ?? new();
            return hosters.Count == 0 ? "ZT" : string.Join(", ", hosters.Select(h => h.Hoster).Take(3));
        }
        catch
        {
            return "ZT";
        }
    }
}
