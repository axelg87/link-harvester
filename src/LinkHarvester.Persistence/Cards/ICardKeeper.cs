namespace LinkHarvester.Persistence.Cards;

/// <summary>
/// The card tables (<see cref="InboxCardEntity"/>, <see cref="CatalogCardEntity"/>,
/// <see cref="CatalogCardGenreEntity"/>, <see cref="CatalogCardLinkFacetEntity"/>)
/// are a denormalised read model: every catalog or inbox endpoint reads
/// exclusively from them, no joins, no per-row subqueries. They mirror the
/// base tables (<c>Titles</c>, <c>Articles</c>, <c>CatalogTitles</c>, etc.)
/// which remain the source of truth.
///
/// The keeper's contract: every base-table write site calls the matching
/// <c>Upsert*</c> / <c>Remove*</c> after <c>SaveChangesAsync</c> on the
/// same <see cref="HarvesterDbContext"/>. The keeper reads the just-written
/// base state and refreshes the card row(s) via a single SQL pass.
///
/// Drift between base and card is healed by <see cref="RebuildAllAsync"/>
/// (initial backfill on startup and admin-triggered reconciliation). The
/// catalog ingester takes the bulk path to amortise the cost of touching
/// every new row.
/// </summary>
public interface ICardKeeper
{
    Task UpsertInboxCardAsync(HarvesterDbContext db, int titleId, CancellationToken ct);
    Task UpsertInboxCardsAsync(HarvesterDbContext db, IReadOnlyCollection<int> titleIds, CancellationToken ct);
    Task RemoveInboxCardAsync(HarvesterDbContext db, int titleId, CancellationToken ct);

    Task UpsertCatalogCardAsync(HarvesterDbContext db, int catalogTitleId, CancellationToken ct);
    Task UpsertCatalogCardsAsync(HarvesterDbContext db, IReadOnlyCollection<int> catalogTitleIds, CancellationToken ct);
    Task RemoveCatalogCardAsync(HarvesterDbContext db, int catalogTitleId, CancellationToken ct);

    /// <summary>
    /// Bulk-rebuild card tables from base. Used by initial backfill and
    /// the reconciler. Idempotent: existing card rows are upserted, orphans
    /// removed. Safe to invoke against a partially populated card schema.
    /// </summary>
    Task RebuildAllAsync(HarvesterDbContext db, CancellationToken ct);
}
