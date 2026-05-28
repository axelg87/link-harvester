using LinkHarvester.Persistence.Catalog;
using Microsoft.AspNetCore.Mvc;

namespace LinkHarvester.Api.Endpoints;

public static class FollowingEndpoints
{
    public static IEndpointRouteBuilder MapFollowingEndpoints(this IEndpointRouteBuilder routes)
    {
        var grp = routes.MapGroup("/api/following").RequireAuthorization();

        grp.MapGet("", async (FollowingService svc, CancellationToken ct) =>
        {
            var items = await svc.GetAllAsync(ct);
            return Results.Ok(items.Select(i => new FollowingItemDto(
                CatalogTitleId: i.CatalogTitleId,
                Title: i.Title,
                Poster: i.Poster,
                Category: i.Category,
                TmdbStatus: i.TmdbStatus,
                LastAirDate: i.LastAirDate,
                NextEpisodeAirDate: i.NextEpisodeAirDate,
                NextEpisodeCode: i.NextEpisodeCode,
                LastGrabbedAt: i.LastGrabbedAt,
                GrabbedEpisodes: i.GrabbedEpisodes,
                AvailableMissingEpisodes: i.AvailableMissingEpisodes)).ToList());
        });

        grp.MapPost("/{titleId:int}/dismiss", async (int titleId, FollowingService svc, CancellationToken ct) =>
        {
            await svc.DismissAsync(titleId, ct);
            return Results.NoContent();
        });

        grp.MapPost("/{titleId:int}/undismiss", async (int titleId, FollowingService svc, CancellationToken ct) =>
        {
            await svc.UndismissAsync(titleId, ct);
            return Results.NoContent();
        });

        return routes;
    }

    public sealed record FollowingItemDto(
        int CatalogTitleId, string Title, string? Poster, string Category,
        string? TmdbStatus, DateTimeOffset? LastAirDate,
        DateTimeOffset? NextEpisodeAirDate, string? NextEpisodeCode,
        DateTimeOffset? LastGrabbedAt,
        List<string> GrabbedEpisodes, List<string> AvailableMissingEpisodes);
}
