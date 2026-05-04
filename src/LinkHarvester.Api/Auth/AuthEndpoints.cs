using System.Security.Claims;
using LinkHarvester.Core;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace LinkHarvester.Api.Auth;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder routes)
    {
        var grp = routes.MapGroup("/api/auth");

        grp.MapPost("/login", async (LoginRequest req, HttpContext ctx, ISettingsService settings) =>
        {
            if (!settings.VerifyCredentials(req.Username ?? string.Empty, req.Password ?? string.Empty))
                return Results.Unauthorized();

            var identity = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.Name, settings.Current.AuthUsername),
                new Claim(ClaimTypes.Role, "user"),
            }, CookieAuthenticationDefaults.AuthenticationScheme);
            await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(identity),
                new AuthenticationProperties { IsPersistent = true, ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30) });
            return Results.Ok(new { user = settings.Current.AuthUsername });
        });

        grp.MapPost("/logout", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Ok();
        });

        grp.MapGet("/me", (HttpContext ctx) =>
        {
            if (ctx.User.Identity?.IsAuthenticated != true) return Results.Unauthorized();
            return Results.Ok(new { user = ctx.User.Identity!.Name });
        });

        return routes;
    }

    public sealed record LoginRequest(string Username, string Password);
}
