using System.Text.Json;
using LinkHarvester.Core;
using LinkHarvester.Persistence;
using LinkHarvester.Persistence.Cards;
using LinkHarvester.Persistence.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LinkHarvester.Worker;

/// <summary>
/// Orchestrates a single scan run for a given source. The pipeline is:
///   1. List the source's "what's new" surface.
///   2. For each candidate article we don't already have: fetch + parse,
///      then upsert Title + Article rows.
///   3. Apply rule-#4: an article that is strictly worse than what's already
///      been submitted for the same Title is auto-skipped (Superseded).
///      An article strictly better than what's already submitted is surfaced
///      as a fresh inbox row.
/// </summary>
public sealed class ScanPipeline
{
    private readonly IDbContextFactory<HarvesterDbContext> _factory;
    private readonly IEnumerable<IFeedSource> _sources;
    private readonly IQualityScorer _scorer;
    private readonly HarvesterCatalogPromoter _catalogPromoter;
    private readonly FollowingDetectionService _followingDetection;
    private readonly ICardKeeper _cards;
    private readonly HarvesterOptions _opts;
    private readonly ILogger<ScanPipeline> _log;

    public ScanPipeline(IDbContextFactory<HarvesterDbContext> factory,
                         IEnumerable<IFeedSource> sources,
                         IQualityScorer scorer,
                         HarvesterCatalogPromoter catalogPromoter,
                         FollowingDetectionService followingDetection,
                         ICardKeeper cards,
                         IOptions<HarvesterOptions> opts,
                         ILogger<ScanPipeline> log)
    {
        _factory = factory;
        _sources = sources;
        _scorer = scorer;
        _catalogPromoter = catalogPromoter;
        _followingDetection = followingDetection;
        _cards = cards;
        _opts = opts.Value;
        _log = log;
    }

    public async Task<ScanRunEntity> RunAsync(string sourceId, CancellationToken ct)
    {
        var source = _sources.FirstOrDefault(s => s.Id == sourceId)
            ?? throw new InvalidOperationException($"No source registered with id '{sourceId}'");

        var run = new ScanRunEntity
        {
            SourceId = sourceId,
            StartedAt = DateTimeOffset.UtcNow
        };
        await using (var db = await _factory.CreateDbContextAsync(ct))
        {
            db.ScanRuns.Add(run);
            await db.SaveChangesAsync(ct);
        }

        try
        {
            var raw = await source.ListNewAsync(ct);
            run.Discovered = raw.Count;

            var processed = 0;
            foreach (var item in raw.Take(_opts.MaxArticlesPerScan))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var inserted = await IngestOneAsync(source, item, ct);
                    if (inserted) run.NewArticles++;
                    processed++;
                }
                catch (Exception ex)
                {
                    run.Failed++;
                    _log.LogWarning(ex, "Failed to ingest {Url}", item.Url);
                }
            }

            run.FinishedAt = DateTimeOffset.UtcNow;
            run.Notes = $"processed={processed}";
        }
        catch (Exception ex)
        {
            run.FinishedAt = DateTimeOffset.UtcNow;
            run.Notes = "fatal: " + ex.Message;
            _log.LogError(ex, "Scan run {Id} for source {Source} failed", run.Id, sourceId);
        }
        finally
        {
            await using var db = await _factory.CreateDbContextAsync(ct);
            db.ScanRuns.Update(run);
            await db.SaveChangesAsync(ct);
        }

        return run;
    }

    /// <summary>
    /// Idempotent: if the (sourceId, externalId) is already known with the same
    /// content hash, this is a no-op. Returns true if a new article row was
    /// inserted (whether for a new title or as a new variant of an existing one).
    /// </summary>
    /// <summary>
    /// Idempotent: fetch + parse + upsert one article. Returns true when a new
    /// row was inserted, false when nothing changed. Internal so the backfill
    /// runner can reuse the same dedup-and-promote path.
    /// </summary>
    internal async Task<bool> IngestOneAsync(IFeedSource source, RawListItem listItem, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);

        var existing = await db.Articles
            .FirstOrDefaultAsync(a => a.SourceId == listItem.SourceId && a.ExternalId == listItem.ExternalId, ct);

        // Fetch + parse to get content hash even for existing rows; we need it
        // to detect mutations (e.g. "season got a new episode").
        var details = await source.FetchArticleAsync(listItem.Url, ct);

        if (existing is not null && existing.ContentHash == details.ContentHash)
        {
            return false;
        }

        var key = source.BuildTitleKey(details);
        var canonical = key.ToCanonical();

        var title = await db.Titles.FirstOrDefaultAsync(t => t.Canonical == canonical, ct);
        if (title is null)
        {
            title = new TitleEntity
            {
                Canonical = canonical,
                NormalizedTitle = key.NormalizedTitle,
                DisplayTitle = details.Title,
                Year = details.Year,
                Kind = details.Kind,
                SeasonNumber = details.SeasonNumber,
                ImdbId = details.ImdbId,
                TmdbId = details.TmdbId,
                Status = TitleStatus.New,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            db.Titles.Add(title);
            await db.SaveChangesAsync(ct);
        }
        else
        {
            title.DisplayTitle = details.Title;
            title.ImdbId ??= details.ImdbId;
            title.TmdbId ??= details.TmdbId;
            title.UpdatedAt = DateTimeOffset.UtcNow;
        }

        // Fall back to listing-card hints if the article body lacked a header.
        var qualityLabel = details.QualityLabel
            ?? listItem.QualityHint
            ?? listItem.LanguageHint;
        var language = details.Language ?? listItem.LanguageHint;

        var rating = _scorer.Score(qualityLabel, language);

        var article = existing ?? new ArticleEntity
        {
            SourceId = details.SourceId,
            ExternalId = details.ExternalId,
            Url = details.Url,
            DiscoveredAt = DateTimeOffset.UtcNow
        };
        article.TitleId = title.Id;
        article.DisplayTitle = details.Title;
        article.QualityLabel = qualityLabel;
        article.QualityScore = rating.Score;
        article.Resolution = rating.Resolution;
        article.Codec = rating.Codec;
        article.SourceTier = rating.Source;
        article.Language = language ?? rating.Language;
        article.SizeBytes = details.SizeBytes;
        article.EpisodeCount = details.EpisodeCount;
        article.AggregatorDlProtectUrl = details.AggregatorDlProtectUrl;
        article.HostersJson = JsonSerializer.Serialize(details.Hosters);
        article.ContentHash = details.ContentHash;

        // Apply rule #4 at ingest time.
        if (title.Status == TitleStatus.Submitted &&
            title.BestSubmittedQualityScore is int submittedScore &&
            article.QualityScore <= submittedScore)
        {
            article.Status = ArticleStatus.Superseded;
        }
        else
        {
            article.Status = ArticleStatus.Parsed;
            if (title.Status == TitleStatus.New)
                title.Status = TitleStatus.InInbox;
        }

        if (existing is null)
            db.Articles.Add(article);
        await db.SaveChangesAsync(ct);

        // Refresh the inbox card after every article. The keeper computes
        // whether this title's visible-articles set is non-empty and writes
        // (or hides) the card row accordingly — that's the only place the
        // chosen-vs-other variants split is decided.
        await _cards.UpsertInboxCardAsync(db, title.Id, ct);

        try
        {
            await _catalogPromoter.PromoteArticleAsync(article.Id, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not promote article {ArticleId} to catalog.", article.Id);
        }

        // Data-only Following detection — writes a row when this article
        // brings an episode the user is already following but hasn't grabbed.
        // No notification surface here; Telegram dispatcher consumes the log.
        await _followingDetection.RegisterIngestedAsync(article.Id, ct);

        return existing is null;
    }
}
