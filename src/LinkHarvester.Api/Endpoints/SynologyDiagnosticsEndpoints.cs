using LinkHarvester.Synology;
using Microsoft.Extensions.Logging;

namespace LinkHarvester.Api.Endpoints;

// THROWAWAY: temporary diagnostics for inventory-design decision. Remove once
// we've decided live-vs-cached inventory strategy.
public static class SynologyDiagnosticsEndpoints
{
    public static IEndpointRouteBuilder MapSynologyDiagnosticsEndpoints(this IEndpointRouteBuilder routes)
    {
        var grp = routes.MapGroup("/api/synology").RequireAuthorization();

        grp.MapPost("/_benchmark", async (
            BenchmarkRequest req,
            IFileStationBenchmarkClient client,
            ILoggerFactory lf,
            CancellationToken ct) =>
        {
            var log = lf.CreateLogger("SynologyDiagnostics");
            var iterations = req.Iterations ?? 10;
            log.LogInformation("FileStation benchmark requested: path={Path} iterations={Iter}", req.Path, iterations);
            var result = await client.RunAsync(req.Path ?? string.Empty, iterations, ct);
            if (result.Ok)
            {
                log.LogInformation(
                    "FileStation benchmark: login={Login}ms cold={Cold}ms warm p50={P50} p95={P95} max={Max} avg={Avg} files={Files} dirs={Dirs} bytes={Bytes}",
                    result.LoginMs, result.ListColdMs,
                    result.ListWarmP50Ms, result.ListWarmP95Ms, result.ListWarmMaxMs, result.ListWarmAvgMs,
                    result.FileCount, result.DirCount, result.ResponseBytes);
            }
            else
            {
                log.LogWarning("FileStation benchmark failed: {Error}", result.Error);
            }
            return Results.Ok(result);
        });

        return routes;
    }

    public sealed record BenchmarkRequest(string? Path, int? Iterations);
}
