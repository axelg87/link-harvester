using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace LinkHarvester.Api.Caching;

/// <summary>
/// HTTP <c>Cache-Control</c> helpers for Minimal APIs. Mirrors the server-side
/// memory cache TTLs so the browser back-button / repeated navigation never
/// crosses the wire — relevant because every API roundtrip is amplified by
/// ~270 ms of France↔Mumbai RTT.
///
/// <c>private</c> because the API is per-user: shared caches (CDNs) must
/// not serve another user's data. <c>no-transform</c> blocks intermediary
/// proxies from re-compressing or mangling the body.
/// </summary>
public static class CacheHeaderExtensions
{
    public static RouteHandlerBuilder CachePrivate(this RouteHandlerBuilder b, int seconds)
    {
        return b.AddEndpointFilter(async (ctx, next) =>
        {
            var result = await next(ctx);
            var headers = ctx.HttpContext.Response.GetTypedHeaders();
            headers.CacheControl = new Microsoft.Net.Http.Headers.CacheControlHeaderValue
            {
                Private = true,
                MaxAge = TimeSpan.FromSeconds(seconds),
                NoTransform = true,
            };
            return result;
        });
    }
}
