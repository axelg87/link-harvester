using System.Globalization;
using LinkHarvester.Persistence;
using LinkHarvester.Persistence.Maintenance;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LinkHarvester.Api.Endpoints;

/// <summary>
/// Diagnostic + manual-action endpoints for the operator. Auth-gated like
/// the rest of /api/*. Kept lean — anything that lives here is something
/// we need to be able to do from a browser when SSH is unavailable.
/// </summary>
public static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder routes)
    {
        var grp = routes.MapGroup("/api/admin").RequireAuthorization();

        // Per-column count of DateTimeOffset rows in known-bad shape, so we
        // can verify the repair actually moved the needle without needing
        // shell access to the SQLite file.
        grp.MapGet("/dto-repair-status", async (
            IDbContextFactory<HarvesterDbContext> factory,
            CancellationToken ct) =>
        {
            var columns = new (string Table, string Column)[]
            {
                ("CatalogTitles", "FirstSeenAt"),
                ("CatalogTitles", "LastSeenAt"),
                ("CatalogLinks",  "CreatedAt"),
            };

            var results = new List<DtoRepairStatusEntry>();
            await using var db = await factory.CreateDbContextAsync(ct);
            var conn = (SqliteConnection)db.Database.GetDbConnection();
            await conn.OpenAsync(ct);

            foreach (var (table, col) in columns)
            {
                using var cmd = conn.CreateCommand();
                // unix-ms range = > 0 AND < 10^15
                // bad-offset    = >= 10^15 AND (low 11 bits) decode to offset outside ±840
                //                 (legal stored bits: 184..1864 since EF stores offset+1024)
                cmd.CommandText =
                    $"SELECT " +
                    $"  COUNT(*), " +
                    $"  SUM(CASE WHEN {col} > 0 AND {col} < 1000000000000000 THEN 1 ELSE 0 END), " +
                    $"  SUM(CASE WHEN {col} >= 1000000000000000 " +
                    $"           AND (({col} & 2047) < 184 OR ({col} & 2047) > 1864) " +
                    $"        THEN 1 ELSE 0 END), " +
                    $"  SUM(CASE WHEN {col} IS NULL THEN 1 ELSE 0 END) " +
                    $"FROM {table}";
                cmd.CommandTimeout = 300;
                using var rdr = await cmd.ExecuteReaderAsync(ct);
                if (!await rdr.ReadAsync(ct)) continue;
                var total = rdr.IsDBNull(0) ? 0 : rdr.GetInt64(0);
                var unix  = rdr.IsDBNull(1) ? 0 : rdr.GetInt64(1);
                var bad   = rdr.IsDBNull(2) ? 0 : rdr.GetInt64(2);
                var nulls = rdr.IsDBNull(3) ? 0 : rdr.GetInt64(3);
                results.Add(new DtoRepairStatusEntry(table, col, total, unix, bad, nulls));
            }

            return Results.Ok(new DtoRepairStatusDto(
                CheckedAt: DateTimeOffset.UtcNow,
                Columns: results));
        });

        // Manual retrigger of the repair, in case the BackgroundService
        // missed a startup pass or the user wants to force a re-scan.
        // Runs synchronously and returns the per-column counts.
        grp.MapPost("/dto-repair-run", async (
            IDbContextFactory<HarvesterDbContext> factory,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var log = loggerFactory.CreateLogger<DateTimeOffsetRepairService>();
            var sw = System.Diagnostics.Stopwatch.StartNew();

            var columns = new (string Table, string Column)[]
            {
                ("CatalogTitles", "FirstSeenAt"),
                ("CatalogTitles", "LastSeenAt"),
                ("CatalogLinks",  "CreatedAt"),
            };

            await using var db = await factory.CreateDbContextAsync(ct);
            var conn = (SqliteConnection)db.Database.GetDbConnection();
            await conn.OpenAsync(ct);

            var report = new List<DtoRepairRunEntry>();
            long totalFixed = 0;
            foreach (var (table, col) in columns)
            {
                var (fixedUnix, fixedBad) = await ManualRepairAsync(conn, table, col, log, ct);
                totalFixed += fixedUnix + fixedBad;
                report.Add(new DtoRepairRunEntry(table, col, fixedUnix, fixedBad));
            }
            sw.Stop();

            log.LogInformation("Manual DateTimeOffset repair: re-encoded {Total} row(s) in {Elapsed}.", totalFixed, sw.Elapsed);
            return Results.Ok(new DtoRepairRunResultDto(totalFixed, sw.Elapsed.ToString(), report));
        });

        return routes;
    }

    private static async Task<(long Unix, long Bad)> ManualRepairAsync(
        SqliteConnection conn, string table, string col,
        ILogger log, CancellationToken ct)
    {
        long fixedUnix, fixedBad;

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                $"UPDATE {table} " +
                $"SET {col} = (621355968000000 + {col} * 10) * 2048 + 1024 " +
                $"WHERE {col} > 0 AND {col} < 1000000000000000";
            cmd.CommandTimeout = (int)TimeSpan.FromMinutes(10).TotalSeconds;
            fixedUnix = await cmd.ExecuteNonQueryAsync(ct);
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                $"UPDATE {table} " +
                $"SET {col} = ({col} & ~2047) | 1024 " +
                $"WHERE {col} >= 1000000000000000 " +
                $"  AND (({col} & 2047) < 184 OR ({col} & 2047) > 1864)";
            cmd.CommandTimeout = (int)TimeSpan.FromMinutes(10).TotalSeconds;
            fixedBad = await cmd.ExecuteNonQueryAsync(ct);
        }

        log.LogInformation("Manual repair: {Table}.{Col} unix-ms fixed={Unix}, bad-offset fixed={Bad}.",
            table, col, fixedUnix, fixedBad);
        return (fixedUnix, fixedBad);
    }

    public sealed record DtoRepairStatusEntry(
        string Table, string Column, long Total, long UnixMsRows, long BadOffsetRows, long NullRows);

    public sealed record DtoRepairStatusDto(
        DateTimeOffset CheckedAt, List<DtoRepairStatusEntry> Columns);

    public sealed record DtoRepairRunEntry(
        string Table, string Column, long FixedUnixMs, long FixedBadOffset);

    public sealed record DtoRepairRunResultDto(
        long TotalFixed, string Elapsed, List<DtoRepairRunEntry> Columns);
}
