using LinkHarvester.Persistence;
using LinkHarvester.Persistence.Maintenance;
using Microsoft.EntityFrameworkCore;

namespace LinkHarvester.Api.Endpoints;

/// <summary>
/// Diagnostic + manual-action endpoints. Auth-gated. Currently only the
/// DateTimeOffset repair surface; will be deleted once the repair is
/// confirmed unneeded.
/// </summary>
public static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder routes)
    {
        var grp = routes.MapGroup("/api/admin").RequireAuthorization();

        // Per-column count of rows in known-bad shape. Same column list as
        // the BackgroundService so adding a new target only requires
        // editing DateTimeOffsetRepairService.Targets.
        grp.MapGet("/dto-repair-status", async (
            IDbContextFactory<HarvesterDbContext> factory,
            CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var results = new List<DtoRepairStatusEntry>();
            foreach (var t in DateTimeOffsetRepairService.Targets)
            {
                var (unix, bad) = await DateTimeOffsetRepairService.CountAsync(db, t.Table, t.Column, ct);
                results.Add(new DtoRepairStatusEntry(t.Table, t.Column, t.Nullable, unix, bad));
            }
            return Results.Ok(new DtoRepairStatusDto(DateTimeOffset.UtcNow, results));
        });

        // Manually trigger the repair, in case the BackgroundService missed
        // a pass or a new column was just added. Synchronous; returns
        // per-column fixed counts.
        grp.MapPost("/dto-repair-run", async (
            IDbContextFactory<HarvesterDbContext> factory,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var log = loggerFactory.CreateLogger<DateTimeOffsetRepairService>();
            var sw = System.Diagnostics.Stopwatch.StartNew();

            await using var db = await factory.CreateDbContextAsync(ct);
            var report = new List<DtoRepairRunEntry>();
            long total = 0;
            foreach (var t in DateTimeOffsetRepairService.Targets)
            {
                var fixedRows = await DateTimeOffsetRepairService.RepairAsync(db, t.Table, t.Column, log, ct);
                total += fixedRows;
                report.Add(new DtoRepairRunEntry(t.Table, t.Column, fixedRows));
            }
            sw.Stop();

            log.LogInformation("Manual DateTimeOffset repair: re-encoded/healed {Total} row(s) in {Elapsed}.",
                total, sw.Elapsed);
            return Results.Ok(new DtoRepairRunResultDto(total, sw.Elapsed.ToString(), report));
        });

        return routes;
    }

    public sealed record DtoRepairStatusEntry(
        string Table, string Column, bool Nullable, long UnixMsRows, long BadOffsetRows);

    public sealed record DtoRepairStatusDto(
        DateTimeOffset CheckedAt, List<DtoRepairStatusEntry> Columns);

    public sealed record DtoRepairRunEntry(string Table, string Column, long FixedRows);

    public sealed record DtoRepairRunResultDto(
        long TotalFixed, string Elapsed, List<DtoRepairRunEntry> Columns);
}
