using Microsoft.EntityFrameworkCore;

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
    }
}
