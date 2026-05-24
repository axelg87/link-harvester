using LinkHarvester.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LinkHarvester.Api.Maintenance;

/// <summary>
/// Single source of truth for the SQL that resets <c>CatalogTitleMetadata</c>
/// rows in the <c>failed</c> bucket back to <c>pending</c>. Two flavours:
/// transient-only (lock/timeout residue from concurrent ingest+enrich runs;
/// see KNOWN_BUGS.md BUG-2) and all-failed.
///
/// Centralising the SQL means the
/// <c>POST /api/catalog/enrichment/reset-failed</c> endpoint, the
/// <c>/api/catalog/stats</c> count, and (eventually) the BUG-2 startup
/// auto-heal all use the exact same pattern set. Tests reference this class
/// directly so a pattern drift in one consumer can't silently mismatch the
/// others.
/// </summary>
public static class EnrichmentMaintenance
{
    /// <summary>
    /// Patterns that match SQLite write-contention or 30-second
    /// command-timeout residue. Inclusive — better to occasionally
    /// re-attempt a genuinely-failed title than to let a contention row
    /// rot in the failed bucket forever.
    /// </summary>
    public const string TransientFailureSqlWhere = @"
        EnrichmentSource = 'failed'
        AND (LastError LIKE '%database is locked%'
             OR LastError LIKE '%database is busy%'
             OR LastError LIKE '%SQLite Error 5%'
             OR LastError LIKE '%SQLite Error 6%'
             OR LastError LIKE '%timeout%'
             OR LastError LIKE '%timed out%')";

    public static Task<int> ResetTransientFailedAsync(HarvesterDbContext db, CancellationToken ct)
        => db.Database.ExecuteSqlRawAsync($@"
            UPDATE CatalogTitleMetadata
            SET EnrichmentSource = 'pending',
                Attempts = 0,
                LastError = NULL
            WHERE {TransientFailureSqlWhere};", ct);

    public static Task<int> ResetAllFailedAsync(HarvesterDbContext db, CancellationToken ct)
        => db.Database.ExecuteSqlRawAsync(@"
            UPDATE CatalogTitleMetadata
            SET EnrichmentSource = 'pending',
                Attempts = 0,
                LastError = NULL
            WHERE EnrichmentSource = 'failed';", ct);

    public static Task<int> CountTransientFailedAsync(HarvesterDbContext db, CancellationToken ct)
        => db.Database.SqlQueryRaw<int>($@"
            SELECT COUNT(*) AS Value
            FROM CatalogTitleMetadata
            WHERE {TransientFailureSqlWhere}").FirstAsync(ct);
}
