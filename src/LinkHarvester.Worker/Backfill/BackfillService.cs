using LinkHarvester.Core;
using LinkHarvester.Persistence;
using LinkHarvester.Persistence.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LinkHarvester.Worker.Backfill;

/// <summary>
/// Orchestrates a resumable historical backfill of one feed source (currently ZT).
/// Walks <see cref="IBackfillFeedSource.ListSinceAsync"/>, hands each item to
/// <see cref="ScanPipeline.IngestOneAsync"/>, and persists progress to
/// <see cref="BackfillRunEntity"/> so we can resume after restart.
/// </summary>
public sealed class BackfillService
{
    private readonly IDbContextFactory<HarvesterDbContext> _factory;
    private readonly IEnumerable<IBackfillFeedSource> _sources;
    private readonly ScanPipeline _scanPipeline;
    private readonly ILogger<BackfillService> _log;

    public BackfillService(
        IDbContextFactory<HarvesterDbContext> factory,
        IEnumerable<IBackfillFeedSource> sources,
        ScanPipeline scanPipeline,
        ILogger<BackfillService> log)
    {
        _factory = factory;
        _sources = sources;
        _scanPipeline = scanPipeline;
        _log = log;
    }

    /// <summary>
    /// Find or create a BackfillRun for the given (source, kind, fromDate),
    /// and walk pages from the last completed page + 1 (or <c>startPage</c> if new).
    ///
    /// Returns the run record as it stood when the walk finished.
    /// </summary>
    public async Task<BackfillRunEntity> RunAsync(
        string sourceId,
        string kind,
        DateTimeOffset fromDate,
        int startPage,
        CancellationToken ct)
    {
        var source = _sources.FirstOrDefault(s => s.Id == sourceId)
            ?? throw new InvalidOperationException($"No backfill source registered with id '{sourceId}'");

        var run = await GetOrCreateRunAsync(sourceId, kind, fromDate, startPage, ct);
        var resumeFrom = Math.Max(startPage, run.LastCompletedPage > 0 ? run.LastCompletedPage : startPage);

        _log.LogInformation(
            "Backfill {Id} starting: source={Source} kind={Kind} from={From:u} resumePage={Page}",
            run.Id, sourceId, kind, fromDate, resumeFrom);

        try
        {
            await foreach (var page in source.ListSinceAsync(kind, fromDate, resumeFrom, ct))
            {
                run.Discovered += page.Items.Count;
                foreach (var item in page.Items)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var inserted = await _scanPipeline.IngestOneAsync(source, item, ct);
                        if (inserted) run.Promoted++;
                        else run.Skipped++;
                        run.LastSeenArticleExternalId = item.ExternalId;
                    }
                    catch (Exception ex)
                    {
                        run.Skipped++;
                        _log.LogWarning(ex, "Backfill ingest failed for {Url}", item.Url);
                    }
                }

                run.LastCompletedPage = page.Page;
                await PersistRunAsync(run, ct);
            }

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
            _log.LogError(ex, "Backfill {Id} failed", run.Id);
        }
        finally
        {
            run.FinishedAt = DateTimeOffset.UtcNow;
            await PersistRunAsync(run, ct);
        }

        return run;
    }

    /// <summary>
    /// Find the most recent run that matches (sourceId, kind, fromDate) and is
    /// in a non-terminal state — otherwise create a fresh row.
    /// </summary>
    public async Task<BackfillRunEntity> GetOrCreateRunAsync(
        string sourceId, string kind, DateTimeOffset fromDate, int startPage, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var existing = await db.BackfillRuns
            .Where(r => r.SourceId == sourceId && r.Kind == kind && r.FromDate == fromDate)
            .OrderByDescending(r => r.Id)
            .FirstOrDefaultAsync(ct);

        if (existing is not null && existing.Status != "succeeded")
        {
            existing.Status = "running";
            existing.Error = null;
            await db.SaveChangesAsync(ct);
            return existing;
        }

        var run = new BackfillRunEntity
        {
            SourceId = sourceId,
            Kind = kind,
            FromDate = fromDate,
            StartPage = startPage,
            StartedAt = DateTimeOffset.UtcNow,
            Status = "running",
        };
        db.BackfillRuns.Add(run);
        await db.SaveChangesAsync(ct);
        return run;
    }

    private async Task PersistRunAsync(BackfillRunEntity run, CancellationToken ct)
    {
        // Re-fetch a tracked entity to avoid stale-tracker exceptions across
        // long-running iterations using a single context factory.
        await using var db = await _factory.CreateDbContextAsync(ct);
        var tracked = await db.BackfillRuns.FirstOrDefaultAsync(r => r.Id == run.Id, ct);
        if (tracked is null) return;
        tracked.LastCompletedPage = run.LastCompletedPage;
        tracked.LastSeenArticleExternalId = run.LastSeenArticleExternalId;
        tracked.Discovered = run.Discovered;
        tracked.Healthy = run.Healthy;
        tracked.Enriched = run.Enriched;
        tracked.Promoted = run.Promoted;
        tracked.Skipped = run.Skipped;
        tracked.Status = run.Status;
        tracked.Error = run.Error;
        tracked.FinishedAt = run.FinishedAt;
        await db.SaveChangesAsync(ct);
    }
}
