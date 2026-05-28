using LinkHarvester.Persistence;
using LinkHarvester.Persistence.Maintenance;
using Microsoft.EntityFrameworkCore;

namespace LinkHarvester.Api.Endpoints;

/// <summary>
/// Operator-only endpoints for the DateTimeOffset repair surface. Will be
/// removed in a cleanup PR once the data is stable.
/// </summary>
public static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder routes)
    {
        var grp = routes.MapGroup("/api/admin").RequireAuthorization();

        // Pre-flight: per-column counts of rows in known-bad states. Single
        // scan, no writes. Run BEFORE dto-repair-run to verify the model.
        grp.MapGet("/dto-repair-status", async (
            IDbContextFactory<HarvesterDbContext> factory,
            CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var results = new List<DtoRepairStatusEntry>();
            foreach (var t in DateTimeOffsetRepairService.Targets)
            {
                var c = await DateTimeOffsetRepairService.CountAsync(db, t.Table, t.Column, ct);
                results.Add(new DtoRepairStatusEntry(t.Table, t.Column, t.Nullable, c.UnixMs, c.BadBits));
            }
            return Results.Ok(new DtoRepairStatusDto(
                CheckedAt: DateTimeOffset.UtcNow,
                TotalBadRows: results.Sum(x => x.UnixMsRows + x.BadOffsetRows),
                Columns: results));
        });

        // Manual full repair. Synchronous, returns per-column fixed counts.
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
                var n = await DateTimeOffsetRepairService.RepairAsync(db, t.Table, t.Column, log, ct);
                total += n;
                report.Add(new DtoRepairRunEntry(t.Table, t.Column, n));
            }
            sw.Stop();
            log.LogInformation("Manual DateTimeOffset repair: re-encoded/healed {Total} row(s) in {Elapsed}.", total, sw.Elapsed);
            return Results.Ok(new DtoRepairRunResultDto(total, sw.Elapsed.ToString(), report));
        });

        return routes;
    }

    public sealed record DtoRepairStatusEntry(
        string Table, string Column, bool Nullable, long UnixMsRows, long BadOffsetRows);

    public sealed record DtoRepairStatusDto(
        DateTimeOffset CheckedAt, long TotalBadRows, List<DtoRepairStatusEntry> Columns);

    public sealed record DtoRepairRunEntry(string Table, string Column, long FixedRows);

    public sealed record DtoRepairRunResultDto(
        long TotalFixed, string Elapsed, List<DtoRepairRunEntry> Columns);
}
