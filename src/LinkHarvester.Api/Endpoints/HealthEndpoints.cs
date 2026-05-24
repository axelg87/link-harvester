using LinkHarvester.Api.Health;

namespace LinkHarvester.Api.Endpoints;

public static class HealthEndpoints
{
    /// <summary>
    /// Hard upper bound on a single health-check pass. Fly's <c>http_service.checks</c>
    /// uses its own timeout (5 s) on top; this guards against an in-process
    /// deadlock that ties up the request thread far longer than that.
    /// </summary>
    private static readonly TimeSpan HealthBudget = TimeSpan.FromSeconds(2);

    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder routes)
    {
        // Anonymous + excluded from request logging: the Fly health probe
        // hits this on a 30 s cadence and would otherwise drown out
        // application log lines in production.
        routes.MapGet("/healthz", async (HealthCheckService health, HttpContext http, CancellationToken ct) =>
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(HealthBudget);

            var report = await health.CheckAsync(budget.Token);
            var status = report.Ok ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable;

            return Results.Json(
                new
                {
                    status = report.Ok ? "ok" : "fail",
                    checks = report.Checks.ToDictionary(
                        kv => kv.Key,
                        kv => new { ok = kv.Value.Ok, detail = kv.Value.Detail })
                },
                statusCode: status);
        })
        .AllowAnonymous()
        .WithName("Healthz")
        .ExcludeFromDescription();

        return routes;
    }
}
