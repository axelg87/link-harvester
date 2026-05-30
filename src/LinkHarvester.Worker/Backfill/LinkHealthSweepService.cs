using LinkHarvester.Persistence;
using LinkHarvester.Persistence.Cards;
using LinkHarvester.Persistence.Catalog;
using LinkHarvester.Resolution.HealthCheck;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LinkHarvester.Worker.Backfill;

/// <summary>
/// Sweep CatalogLinks, probing each against its hoster's per-host matcher and
/// recording the verdict on the link row. After each sweep, titles whose links
/// are *all* verifiably Dead are flagged hidden (auditable, never deleted).
/// </summary>
public sealed class LinkHealthSweepService
{
    /// <summary>
    /// Maximum number of links checked in a single batch before flushing
    /// counters and yielding control. Keeps SQLite write transactions short.
    /// </summary>
    public const int DefaultBatchSize = 200;

    private readonly IDbContextFactory<HarvesterDbContext> _factory;
    private readonly ILinkHealthService _health;
    private readonly ICardKeeper _cards;
    private readonly ILogger<LinkHealthSweepService> _log;

    public LinkHealthSweepService(
        IDbContextFactory<HarvesterDbContext> factory,
        ILinkHealthService health,
        ICardKeeper cards,
        ILogger<LinkHealthSweepService> log)
    {
        _factory = factory;
        _cards = cards;
        _health = health;
        _log = log;
    }

    public async Task<HealthSweepRunEntity> RunAsync(
        string? hosterFilter,
        bool resume,
        CancellationToken ct)
    {
        var run = await GetOrCreateRunAsync(hosterFilter, resume, ct);
        _log.LogInformation(
            "Health sweep {Id} starting: hoster={Hoster} resumeFromLinkId={From}",
            run.Id, hosterFilter ?? "<any>", run.LastCheckedCatalogLinkId);

        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var batch = await NextBatchAsync(run, ct);
                if (batch.Count == 0) break;

                foreach (var link in batch)
                {
                    ct.ThrowIfCancellationRequested();
                    var result = await _health.CheckAsync(link.LinkUrl, link.HostName, ct);
                    link.HealthStatus = result.Health.ToString();
                    link.HealthCheckedAt = DateTimeOffset.UtcNow;
                    link.HealthSignature = result.Signature;

                    run.Checked++;
                    switch (result.Health)
                    {
                        case LinkHealth.Alive: run.Alive++; break;
                        case LinkHealth.Dead: run.Dead++; break;
                        default: run.Unknown++; break;
                    }
                    run.LastCheckedCatalogLinkId = link.Id;
                }

                await PersistBatchAsync(run, batch, ct);
            }

            // After the sweep, mark titles whose every link is Dead as hidden.
            run.HiddenTitles += await HideDeadTitlesAsync(ct);
            run.Status = "succeeded";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            run.Status = "cancelled";
        }
        catch (Exception ex)
        {
            run.Status = "failed";
            run.Error = ex.Message;
            _log.LogError(ex, "Health sweep {Id} failed", run.Id);
        }
        finally
        {
            run.FinishedAt = DateTimeOffset.UtcNow;
            await PersistRunAsync(run, ct);
        }

        return run;
    }

    public async Task<HealthSweepRunEntity> GetOrCreateRunAsync(string? hosterFilter, bool resume, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        if (resume)
        {
            var existing = await db.HealthSweepRuns
                .Where(r => r.HosterFilter == hosterFilter && r.Status != "succeeded")
                .OrderByDescending(r => r.Id)
                .FirstOrDefaultAsync(ct);
            if (existing is not null)
            {
                existing.Status = "running";
                existing.Error = null;
                await db.SaveChangesAsync(ct);
                return existing;
            }
        }

        var run = new HealthSweepRunEntity
        {
            StartedAt = DateTimeOffset.UtcNow,
            Status = "running",
            HosterFilter = hosterFilter,
        };
        db.HealthSweepRuns.Add(run);
        await db.SaveChangesAsync(ct);
        return run;
    }

    private async Task<List<CatalogLinkEntity>> NextBatchAsync(HealthSweepRunEntity run, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var q = db.CatalogLinks.Where(l => l.Id > run.LastCheckedCatalogLinkId);
        if (!string.IsNullOrEmpty(run.HosterFilter))
        {
            var needle = run.HosterFilter.ToLowerInvariant();
            q = q.Where(l => l.HostName.ToLower() == needle || l.NormalizedHost == needle);
        }
        return await q.OrderBy(l => l.Id).Take(DefaultBatchSize).ToListAsync(ct);
    }

    private async Task PersistBatchAsync(HealthSweepRunEntity run, List<CatalogLinkEntity> batch, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        // Re-attach by Id to apply our in-memory mutations.
        foreach (var l in batch)
        {
            db.CatalogLinks.Attach(l);
            db.Entry(l).Property(x => x.HealthStatus).IsModified = true;
            db.Entry(l).Property(x => x.HealthCheckedAt).IsModified = true;
            db.Entry(l).Property(x => x.HealthSignature).IsModified = true;
        }
        await db.SaveChangesAsync(ct);

        await using var db2 = await _factory.CreateDbContextAsync(ct);
        var tracked = await db2.HealthSweepRuns.FirstOrDefaultAsync(r => r.Id == run.Id, ct);
        if (tracked is not null)
        {
            tracked.LastCheckedCatalogLinkId = run.LastCheckedCatalogLinkId;
            tracked.Checked = run.Checked;
            tracked.Alive = run.Alive;
            tracked.Dead = run.Dead;
            tracked.Unknown = run.Unknown;
            await db2.SaveChangesAsync(ct);
        }
    }

    private async Task<int> HideDeadTitlesAsync(CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        // A title is hidden when every link has a non-null status AND every
        // status equals "Dead". Titles with at least one Alive/Unknown stay visible.
        var candidates = await db.CatalogTitles
            .Where(t => !t.IsHidden && t.Links.Count > 0 &&
                        t.Links.All(l => l.HealthStatus == nameof(LinkHealth.Dead)))
            .ToListAsync(ct);
        foreach (var t in candidates)
        {
            t.IsHidden = true;
            t.HiddenReason = "all-links-dead";
        }
        if (candidates.Count > 0)
        {
            await db.SaveChangesAsync(ct);
            await _cards.UpsertCatalogCardsAsync(db, candidates.Select(t => t.Id).ToList(), ct);
        }
        return candidates.Count;
    }

    private async Task PersistRunAsync(HealthSweepRunEntity run, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var tracked = await db.HealthSweepRuns.FirstOrDefaultAsync(r => r.Id == run.Id, ct);
        if (tracked is null) return;
        tracked.Checked = run.Checked;
        tracked.Alive = run.Alive;
        tracked.Dead = run.Dead;
        tracked.Unknown = run.Unknown;
        tracked.HiddenTitles = run.HiddenTitles;
        tracked.LastCheckedCatalogLinkId = run.LastCheckedCatalogLinkId;
        tracked.Status = run.Status;
        tracked.Error = run.Error;
        tracked.FinishedAt = run.FinishedAt;
        await db.SaveChangesAsync(ct);
    }
}
