using LinkHarvester.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LinkHarvester.Persistence.Cards;

/// <summary>
/// Runs once at startup. Rebuilds all card rows from base tables on first
/// boot after the read-model migration, then sets
/// <c>AppSettingsEntity.CardsBackfilledAt</c> so subsequent boots are no-ops.
/// On every boot it flips <see cref="CardSyncState.IsReady"/> once cards are
/// proven present, which gates the v2 endpoints.
///
/// Runs as a fire-and-forget task to keep Kestrel's health check responsive
/// during a multi-minute initial backfill of a 100k+ catalog. Until it
/// completes the v2 endpoints respond 503 and the WASM client falls back to
/// the legacy reader path.
/// </summary>
public sealed class CardBackfillService
{
    private readonly IDbContextFactory<HarvesterDbContext> _dbf;
    private readonly ICardKeeper _keeper;
    private readonly CardSyncState _state;
    private readonly ILogger<CardBackfillService> _log;

    public CardBackfillService(
        IDbContextFactory<HarvesterDbContext> dbf,
        ICardKeeper keeper,
        CardSyncState state,
        ILogger<CardBackfillService> log)
    {
        _dbf = dbf;
        _keeper = keeper;
        _state = state;
        _log = log;
    }

    public async Task EnsureBackfilledAsync(CancellationToken ct)
    {
        using var db = await _dbf.CreateDbContextAsync(ct);
        var settings = await db.AppSettings.FirstOrDefaultAsync(ct);
        if (settings is null)
        {
            _log.LogWarning("card backfill skipped: AppSettings row missing");
            return;
        }

        // Fast path: backfill stamped in a prior boot → cards are proven to
        // exist; flip the state to ready and exit. CardKeeper has been
        // maintaining the tables on every write since.
        if (settings.CardsBackfilledAt is not null)
        {
            _state.MarkReady();
            _log.LogInformation("card read model ready (backfilled {When:o})",
                settings.CardsBackfilledAt.Value);
            return;
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        _log.LogInformation("card read model: first-boot backfill starting");
        try
        {
            await _keeper.RebuildAllAsync(db, ct);

            settings.CardsBackfilledAt = DateTimeOffset.UtcNow;
            settings.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            _state.MarkReady();
            _log.LogInformation("card read model backfilled in {Elapsed}", stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            // Don't crash the host — v2 endpoints stay 503 until next attempt.
            _log.LogError(ex, "card read model backfill failed after {Elapsed}", stopwatch.Elapsed);
        }
    }
}
