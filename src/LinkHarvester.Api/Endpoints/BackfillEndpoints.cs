using LinkHarvester.Persistence;
using LinkHarvester.Worker.Backfill;
using Microsoft.EntityFrameworkCore;

namespace LinkHarvester.Api.Endpoints;

public static class BackfillEndpoints
{
    public static IEndpointRouteBuilder MapBackfillEndpoints(this IEndpointRouteBuilder routes)
    {
        var grp = routes.MapGroup("/api/backfill").RequireAuthorization();

        // POST /api/backfill/start { source, kind, fromDate, startPage }
        grp.MapPost("/start", (StartBackfillReq req, BackfillJobRunner runner) =>
        {
            if (string.IsNullOrWhiteSpace(req.Source) || string.IsNullOrWhiteSpace(req.Kind))
                return Results.BadRequest(new { error = "source and kind required" });
            if (!DateTimeOffset.TryParse(req.FromDate, out var from))
                return Results.BadRequest(new { error = "fromDate must be ISO-8601" });

            var startPage = req.StartPage is > 0 ? req.StartPage.Value : 1;
            if (!runner.TryStartBackfill(req.Source, req.Kind, from, startPage, out var reason))
                return Results.Conflict(new { error = reason });
            return Results.Accepted("/api/backfill/status");
        });

        grp.MapPost("/cancel", (BackfillJobRunner runner) =>
        {
            runner.CancelBackfill();
            return Results.Ok();
        });

        grp.MapGet("/status", (BackfillJobRunner runner) => Results.Ok(runner.BackfillSnapshot()));

        // History across all runs (latest first).
        grp.MapGet("/runs", async (IDbContextFactory<HarvesterDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var rows = await db.BackfillRuns
                .OrderByDescending(r => r.Id)
                .Take(20)
                .Select(r => new
                {
                    r.Id, r.SourceId, r.Kind, r.FromDate, r.StartPage, r.LastCompletedPage,
                    r.Discovered, r.Promoted, r.Skipped, r.Status, r.Error,
                    r.StartedAt, r.FinishedAt,
                })
                .ToListAsync(ct);
            return Results.Ok(rows);
        });

        // POST /api/backfill/sweep/start { hosterFilter?, resume? }
        var sweep = routes.MapGroup("/api/backfill/sweep").RequireAuthorization();
        sweep.MapPost("/start", (StartSweepReq? req, BackfillJobRunner runner) =>
        {
            var resume = req?.Resume ?? false;
            if (!runner.TryStartSweep(req?.HosterFilter, resume, out var reason))
                return Results.Conflict(new { error = reason });
            return Results.Accepted("/api/backfill/sweep/status");
        });
        sweep.MapPost("/cancel", (BackfillJobRunner runner) =>
        {
            runner.CancelSweep();
            return Results.Ok();
        });
        sweep.MapGet("/status", (BackfillJobRunner runner) => Results.Ok(runner.SweepSnapshot()));
        sweep.MapGet("/runs", async (IDbContextFactory<HarvesterDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var rows = await db.HealthSweepRuns
                .OrderByDescending(r => r.Id)
                .Take(20)
                .Select(r => new
                {
                    r.Id, r.HosterFilter, r.LastCheckedCatalogLinkId,
                    r.Checked, r.Alive, r.Dead, r.Unknown, r.HiddenTitles,
                    r.Status, r.Error, r.StartedAt, r.FinishedAt,
                })
                .ToListAsync(ct);
            return Results.Ok(rows);
        });

        // GET /api/backfill/recent?take=5 — last titles promoted to catalog from harvester.
        // We surface the freshest catalog rows (any source) so the user can spot-check.
        grp.MapGet("/recent", async (IDbContextFactory<HarvesterDbContext> factory, int? take, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var n = Math.Clamp(take ?? 5, 1, 25);
            var rows = await db.CatalogTitles
                .Where(t => t.CanonicalKey.StartsWith("harvester:"))
                .OrderByDescending(t => t.FirstSeenAt)
                .Take(n)
                .Select(t => new
                {
                    t.Id, t.TitleName, t.CategoryName, t.TitlePoster,
                    t.LinkCount, t.IsHidden, t.HiddenReason,
                    t.FirstSeenAt, t.LastSeenAt,
                    MetaYear = t.Metadata != null ? t.Metadata.Year : (int?)null,
                    EnrichmentSource = t.Metadata != null ? t.Metadata.EnrichmentSource : null,
                })
                .ToListAsync(ct);
            return Results.Ok(rows);
        });

        return routes;
    }

    public sealed record StartBackfillReq(string Source, string Kind, string FromDate, int? StartPage);
    public sealed record StartSweepReq(string? HosterFilter, bool? Resume);
}
