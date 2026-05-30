using LinkHarvester.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LinkHarvester.Persistence.Cards;

/// <summary>
/// Runs once at startup. Cheap stamp check — does NOT rebuild cards
/// automatically. If the prior boot stamped <c>AppSettingsEntity.CardsBackfilledAt</c>,
/// the in-process <see cref="CardSyncState"/> flips to ready and the
/// fast-path endpoints activate; otherwise the cold path keeps serving and
/// an operator must trigger <c>POST /api/diag/cards/rebuild</c> to populate
/// the read model.
///
/// History: the v42 deploy auto-triggered a background rebuild on first
/// boot; an early exception (likely DbContext creation or AppSettings
/// query) threw before the first log line and was swallowed by Task.Run's
/// unobserved-exception handling. Cards stayed empty, the stamp stayed
/// null, the fast path stayed dark. Removing the auto-trigger means we
/// observe + acknowledge the rebuild deliberately the first time, and a
/// restart can never silently re-launch it.
/// </summary>
public sealed class CardBackfillService
{
    private readonly IDbContextFactory<HarvesterDbContext> _dbf;
    private readonly CardSyncState _state;
    private readonly ILogger<CardBackfillService> _log;

    public CardBackfillService(
        IDbContextFactory<HarvesterDbContext> dbf,
        CardSyncState state,
        ILogger<CardBackfillService> log)
    {
        _dbf = dbf;
        _state = state;
        _log = log;
    }

    public async Task EnsureReadyAsync(CancellationToken ct)
    {
        // Whole body in a single try — early exceptions during DbContext
        // creation or settings query MUST be logged. The boot caller is
        // synchronous so it can decide what to do on failure; either way,
        // the v2 endpoints stay dark until the stamp exists.
        try
        {
            using var db = await _dbf.CreateDbContextAsync(ct);
            var settings = await db.AppSettings.AsNoTracking().FirstOrDefaultAsync(ct);
            if (settings is null)
            {
                _log.LogWarning("card sync state: AppSettings row missing; cold path stays active");
                return;
            }

            if (settings.CardsBackfilledAt is not null)
            {
                _state.MarkReady();
                _log.LogInformation("card read model ready (backfilled {When:o})",
                    settings.CardsBackfilledAt.Value);
                return;
            }

            _log.LogWarning(
                "card read model NOT YET BACKFILLED — cold-path serving. " +
                "POST /api/diag/cards/rebuild to populate.");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "card sync state probe failed");
        }
    }
}
