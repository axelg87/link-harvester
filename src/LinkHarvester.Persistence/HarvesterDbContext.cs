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
            e.Property(t => t.Canonical).HasMaxLength(512);
            e.Property(t => t.NormalizedTitle).HasMaxLength(512);
            e.Property(t => t.DisplayTitle).HasMaxLength(512);
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
            e.HasOne(s => s.Article).WithMany().HasForeignKey(s => s.ArticleId)
                .OnDelete(DeleteBehavior.Cascade);
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
            e.HasIndex(l => l.NormalizedHost);
            e.HasIndex(l => l.QualityName);
            e.Property(l => l.LinkUrl).HasMaxLength(2048);
            e.Property(l => l.HostName).HasMaxLength(64);
            e.Property(l => l.NormalizedHost).HasMaxLength(64);
            e.Property(l => l.QualityName).HasMaxLength(64);
            e.Property(l => l.AudioLangs).HasMaxLength(128);
            e.Property(l => l.SubLangs).HasMaxLength(128);
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
    }
}
