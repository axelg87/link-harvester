using LinkHarvester.Persistence.Cards;
using LinkHarvester.Persistence.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace LinkHarvester.Persistence;

public class HarvesterDbContext : DbContext
{
    public HarvesterDbContext(DbContextOptions<HarvesterDbContext> options) : base(options) { }

    public DbSet<TitleEntity> Titles => Set<TitleEntity>();
    public DbSet<ArticleEntity> Articles => Set<ArticleEntity>();
    public DbSet<ResolvedLinkEntity> ResolvedLinks => Set<ResolvedLinkEntity>();
    public DbSet<SubmissionEntity> Submissions => Set<SubmissionEntity>();
    public DbSet<ScanRunEntity> ScanRuns => Set<ScanRunEntity>();
    public DbSet<CapSolverSpendEntity> CapSolverSpends => Set<CapSolverSpendEntity>();
    public DbSet<AppSettingsEntity> AppSettings => Set<AppSettingsEntity>();

    public DbSet<CatalogTitleEntity> CatalogTitles => Set<CatalogTitleEntity>();
    public DbSet<CatalogEpisodeEntity> CatalogEpisodes => Set<CatalogEpisodeEntity>();
    public DbSet<CatalogLinkEntity> CatalogLinks => Set<CatalogLinkEntity>();
    public DbSet<CatalogTitleMetadataEntity> CatalogTitleMetadata => Set<CatalogTitleMetadataEntity>();
    public DbSet<CatalogImportRunEntity> CatalogImportRuns => Set<CatalogImportRunEntity>();
    public DbSet<BackfillRunEntity> BackfillRuns => Set<BackfillRunEntity>();
    public DbSet<HealthSweepRunEntity> HealthSweepRuns => Set<HealthSweepRunEntity>();
    public DbSet<FollowingDismissalEntity> FollowingDismissals => Set<FollowingDismissalEntity>();
    public DbSet<FollowingDetectionLogEntity> FollowingDetectionLog => Set<FollowingDetectionLogEntity>();
    public DbSet<DiscoveryEntryEntity> DiscoveryEntries => Set<DiscoveryEntryEntity>();

    // Read-model card tables. Owned by ICardKeeper; readers query these
    // directly with zero joins. See Cards/CardEntities.cs for the data shape.
    public DbSet<InboxCardEntity> InboxCards => Set<InboxCardEntity>();
    public DbSet<CatalogCardEntity> CatalogCards => Set<CatalogCardEntity>();
    public DbSet<CatalogCardGenreEntity> CatalogCardGenres => Set<CatalogCardGenreEntity>();
    public DbSet<CatalogCardLinkFacetEntity> CatalogCardLinkFacets => Set<CatalogCardLinkFacetEntity>();

    protected override void ConfigureConventions(ModelConfigurationBuilder cfg)
    {
        // SQLite cannot ORDER BY DateTimeOffset; persist as long (UnixTimeMilliseconds).
        cfg.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();
        cfg.Properties<DateTimeOffset?>().HaveConversion<DateTimeOffsetToBinaryConverter>();
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<TitleEntity>(e =>
        {
            e.HasIndex(t => t.Canonical).IsUnique();
            e.HasIndex(t => new { t.NormalizedTitle, t.Year, t.Kind, t.SeasonNumber });
            // Inbox query filters by Status and orders by UpdatedAt; without
            // this composite index it's a full table scan that grows linearly
            // with scan history.
            e.HasIndex(t => new { t.Status, t.UpdatedAt });
            e.HasIndex(t => t.CatalogTitleId);
            e.Property(t => t.Canonical).HasMaxLength(512);
            e.Property(t => t.NormalizedTitle).HasMaxLength(512);
            e.Property(t => t.DisplayTitle).HasMaxLength(512);
            e.Property(t => t.ImdbId).HasMaxLength(32);
        });

        b.Entity<ArticleEntity>(e =>
        {
            e.HasIndex(a => new { a.SourceId, a.ExternalId }).IsUnique();
            e.HasIndex(a => a.Status);
            e.Property(a => a.SourceId).HasMaxLength(64);
            e.Property(a => a.ExternalId).HasMaxLength(128);
            e.Property(a => a.Url).HasMaxLength(1024);
            e.Property(a => a.AggregatorDlProtectUrl).HasMaxLength(1024);
            e.Property(a => a.ContentHash).HasMaxLength(128);
            e.HasOne(a => a.Title).WithMany(t => t.Articles).HasForeignKey(a => a.TitleId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<ResolvedLinkEntity>(e =>
        {
            e.HasIndex(r => r.ArticleId);
            e.Property(r => r.Hoster).HasMaxLength(64);
            e.Property(r => r.Url).HasMaxLength(2048);
            e.HasOne(r => r.Article).WithMany(a => a.ResolvedLinks).HasForeignKey(r => r.ArticleId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<SubmissionEntity>(e =>
        {
            e.HasIndex(s => s.ArticleId);
            e.HasIndex(s => new { s.Source, s.SubmittedAt });
            e.HasIndex(s => new { s.Status, s.SubmittedAt });
            e.HasIndex(s => s.CatalogTitleId);
            e.Property(s => s.DisplayTitle).HasMaxLength(512);
            e.Property(s => s.Destination).HasMaxLength(256);
            e.Property(s => s.ResponseMessage).HasMaxLength(1024);
            e.Property(s => s.DsmFailedUrl).HasMaxLength(2048);
            e.Property(s => s.CatalogLinkIdsJson).HasMaxLength(1024);
            e.HasOne(s => s.Article).WithMany().HasForeignKey(s => s.ArticleId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<ScanRunEntity>(e =>
        {
            e.HasIndex(s => new { s.SourceId, s.StartedAt });
        });

        b.Entity<CapSolverSpendEntity>(e =>
        {
            e.HasIndex(c => new { c.Year, c.Month }).IsUnique();
        });

        b.Entity<AppSettingsEntity>(e =>
        {
            e.Property(s => s.SynologyBaseUrl).HasMaxLength(256);
            e.Property(s => s.SynologyQuickConnectId).HasMaxLength(128);
            e.Property(s => s.SynologyResolvedBaseUrl).HasMaxLength(256);
            e.Property(s => s.SynologyUsername).HasMaxLength(128);
            e.Property(s => s.SynologyPasswordEncrypted).HasMaxLength(2048);
            e.Property(s => s.AuthUsername).HasMaxLength(128);
            e.Property(s => s.AuthPasswordEncrypted).HasMaxLength(2048);
            e.Property(s => s.HosterPriorityCsv).HasMaxLength(512);
            e.Property(s => s.SynologyMovieDestination).HasMaxLength(256);
            e.Property(s => s.SynologySeriesDestination).HasMaxLength(256);
            e.Property(s => s.TmdbApiKeyEncrypted).HasMaxLength(2048);
        });

        b.Entity<CatalogTitleEntity>(e =>
        {
            e.HasIndex(t => t.CanonicalKey).IsUnique();
            e.HasIndex(t => t.NormalizedTitle);
            e.HasIndex(t => t.TmdbId);
            e.HasIndex(t => t.ImdbId);
            e.HasIndex(t => t.CategoryName);
            e.Property(t => t.CanonicalKey).HasMaxLength(256);
            e.Property(t => t.TitleName).HasMaxLength(512);
            e.Property(t => t.OriginalTitle).HasMaxLength(512);
            e.Property(t => t.NormalizedTitle).HasMaxLength(512);
            e.Property(t => t.ImdbId).HasMaxLength(32);
            e.Property(t => t.CategoryName).HasMaxLength(64);
            e.Property(t => t.TitlePoster).HasMaxLength(512);
            e.Property(t => t.HiddenReason).HasMaxLength(128);
            e.HasIndex(t => t.IsHidden);
        });

        b.Entity<CatalogEpisodeEntity>(e =>
        {
            e.HasIndex(ep => new { ep.TitleId, ep.SeasonNumber, ep.EpisodeNumber, ep.IsFullSeason }).IsUnique();
            e.Property(ep => ep.EpisodeName).HasMaxLength(512);
            e.Property(ep => ep.EpisodePoster).HasMaxLength(512);
            e.HasOne(ep => ep.Title).WithMany(t => t.Episodes).HasForeignKey(ep => ep.TitleId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<CatalogLinkEntity>(e =>
        {
            e.HasIndex(l => l.ExternalLinkId).IsUnique();
            e.HasIndex(l => l.TitleId);
            e.HasIndex(l => l.EpisodeId);
            e.HasIndex(l => l.HarvesterArticleId);
            e.HasIndex(l => l.LinkSource);
            e.HasIndex(l => l.NormalizedHost);
            e.HasIndex(l => l.QualityName);
            // Composite that backs /api/catalog/search's 4-way filter
            // (t.Links.Any(l => l.NormalizedHost == ... && l.QualityName == ...
            //                  && l.AudioLangs.Contains(...) && l.SizeBytes >= ...)).
            // Without it SQLite must re-scan every link row per title under
            // the EXISTS subquery — for series with hundreds of links this
            // is the dominant cost when any catalog filter is engaged.
            e.HasIndex(l => new { l.TitleId, l.NormalizedHost, l.QualityName });
            e.Property(l => l.LinkUrl).HasMaxLength(2048);
            e.Property(l => l.LinkSource).HasMaxLength(32);
            e.Property(l => l.HostName).HasMaxLength(64);
            e.Property(l => l.NormalizedHost).HasMaxLength(64);
            e.Property(l => l.QualityName).HasMaxLength(64);
            e.Property(l => l.AudioLangs).HasMaxLength(128);
            e.Property(l => l.SubLangs).HasMaxLength(128);
            e.Property(l => l.HealthStatus).HasMaxLength(16);
            e.Property(l => l.HealthSignature).HasMaxLength(256);
            e.HasIndex(l => l.HealthStatus);
            e.HasOne(l => l.Title).WithMany(t => t.Links).HasForeignKey(l => l.TitleId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(l => l.Episode).WithMany(ep => ep.Links).HasForeignKey(l => l.EpisodeId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<CatalogTitleMetadataEntity>(e =>
        {
            e.HasIndex(m => m.TitleId).IsUnique();
            e.HasIndex(m => m.EnrichmentSource);
            e.HasIndex(m => m.Year);
            // Default catalog browse sort is Meta.Popularity DESC. Without
            // this index SQLite must scan all 100k+ metadata rows then sort
            // — the dominant cost at /api/catalog/search?sort=popularity.
            e.HasIndex(m => m.Popularity);
            // Rating-sorted browse path.
            e.HasIndex(m => m.VoteAverage);
            e.Property(m => m.ReleaseDate).HasMaxLength(20);
            e.Property(m => m.OriginalLanguage).HasMaxLength(16);
            e.Property(m => m.Status).HasMaxLength(32);
            e.Property(m => m.EnrichmentSource).HasMaxLength(32);
            e.Property(m => m.LastError).HasMaxLength(512);
            e.HasOne(m => m.Title).WithOne(t => t.Metadata).HasForeignKey<CatalogTitleMetadataEntity>(m => m.TitleId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<CatalogImportRunEntity>(e =>
        {
            e.HasIndex(r => r.StartedAt);
            e.Property(r => r.Source).HasMaxLength(32);
            e.Property(r => r.SourceDescription).HasMaxLength(512);
            e.Property(r => r.Status).HasMaxLength(32);
            e.Property(r => r.Notes).HasMaxLength(1024);
        });

        b.Entity<BackfillRunEntity>(e =>
        {
            e.HasIndex(r => new { r.SourceId, r.Kind, r.StartedAt });
            e.Property(r => r.SourceId).HasMaxLength(32);
            e.Property(r => r.Kind).HasMaxLength(32);
            e.Property(r => r.Status).HasMaxLength(16);
            e.Property(r => r.LastSeenArticleExternalId).HasMaxLength(64);
            e.Property(r => r.Error).HasMaxLength(1024);
        });

        b.Entity<HealthSweepRunEntity>(e =>
        {
            e.HasIndex(r => r.StartedAt);
            e.Property(r => r.Status).HasMaxLength(16);
            e.Property(r => r.HosterFilter).HasMaxLength(64);
            e.Property(r => r.Error).HasMaxLength(1024);
        });

        b.Entity<FollowingDismissalEntity>(e =>
        {
            e.HasIndex(d => d.CatalogTitleId).IsUnique();
        });

        b.Entity<FollowingDetectionLogEntity>(e =>
        {
            e.HasIndex(d => d.CatalogTitleId);
            e.HasIndex(d => new { d.ConsumedAt, d.DetectedAt });
            // Detection is idempotent per (title, episode-code) — a re-ingest
            // of the same article during backfill must not produce a second
            // notification.
            e.HasIndex(d => new { d.CatalogTitleId, d.EpisodeCode }).IsUnique();
            e.Property(d => d.EpisodeCode).HasMaxLength(16);
            e.Property(d => d.ConsumedBy).HasMaxLength(32);
        });

        b.Entity<DiscoveryEntryEntity>(e =>
        {
            // Replaceable: refresh worker upserts by (CatalogTitleId, Source).
            e.HasIndex(d => new { d.CatalogTitleId, d.Source }).IsUnique();
            e.HasIndex(d => new { d.Source, d.Rank });
            e.HasIndex(d => d.FetchedAt);
            e.Property(d => d.Source).HasMaxLength(48);
            e.Property(d => d.Reason).HasMaxLength(128);
        });

        // ─── Read-model card tables ──────────────────────────────────────────
        b.Entity<InboxCardEntity>(e =>
        {
            e.HasKey(c => c.TitleId);
            // /api/inbox reads: WHERE Visible = 1 ORDER BY UpdatedAt DESC LIMIT 200.
            // This composite is the only index that query touches.
            e.HasIndex(c => new { c.Visible, c.UpdatedAt });
            e.Property(c => c.DisplayTitle).HasMaxLength(512);
            e.Property(c => c.Kind).HasMaxLength(16);
        });

        b.Entity<CatalogCardEntity>(e =>
        {
            e.HasKey(c => c.TitleId);
            // Default sort: ORDER BY Popularity DESC, TitleName.
            // SQLite index direction defaults to ASC; including IsHidden first
            // lets the filter use the same index. The popularity DESC scan is
            // backwards through the B-tree but still O(log n + page).
            e.HasIndex(c => new { c.IsHidden, c.Popularity });
            e.HasIndex(c => new { c.IsHidden, c.Year });
            e.HasIndex(c => new { c.IsHidden, c.Rating });
            e.HasIndex(c => new { c.IsHidden, c.NormalizedTitle });
            e.HasIndex(c => c.CategoryName);
            e.HasIndex(c => c.OriginalLanguage);
            e.HasIndex(c => c.HasMetadata);
            e.Property(c => c.TitleName).HasMaxLength(512);
            e.Property(c => c.OriginalTitle).HasMaxLength(512);
            e.Property(c => c.NormalizedTitle).HasMaxLength(512);
            e.Property(c => c.CategoryName).HasMaxLength(64);
            e.Property(c => c.TitlePoster).HasMaxLength(512);
            e.Property(c => c.OriginalLanguage).HasMaxLength(16);
            e.Property(c => c.EnrichmentSource).HasMaxLength(32);
        });

        b.Entity<CatalogCardGenreEntity>(e =>
        {
            e.HasKey(g => new { g.CardId, g.Genre });
            // The genre filter does a semi-join from cards onto this table;
            // (Genre, CardId) gives index-only access for that direction.
            e.HasIndex(g => new { g.Genre, g.CardId });
            e.Property(g => g.Genre).HasMaxLength(64);
        });

        b.Entity<CatalogCardLinkFacetEntity>(e =>
        {
            // Indices cover the four filter dimensions. The host+quality+card
            // composite plus the (NormalizedHost,CardId) and (QualityName,CardId)
            // covers the search EXISTS subquery. The (AudioLangs,CardId) index
            // is what makes the /facets GROUP BY AudioLangs over the full 2.3M
            // facet rows finish in ~1s instead of ~20s — at write time it's a
            // single extra entry per row, well worth it.
            e.HasIndex(f => new { f.NormalizedHost, f.CardId });
            e.HasIndex(f => new { f.QualityName, f.CardId });
            e.HasIndex(f => new { f.AudioLangs, f.CardId });
            e.HasIndex(f => new { f.CardId, f.NormalizedHost, f.QualityName });
            e.Property(f => f.NormalizedHost).HasMaxLength(64);
            e.Property(f => f.QualityName).HasMaxLength(64);
            e.Property(f => f.AudioLangs).HasMaxLength(128);
        });
    }
}
