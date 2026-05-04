using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace LinkHarvester.Api.Auth;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder routes)
    {
        var grp = routes.MapGroup("/api/auth");

        grp.MapPost("/login", async (LoginRequest req, HttpContext ctx, IOptions<AuthOptions> opts) =>
        {
            var o = opts.Value;
            if (!string.Equals(req.Username, o.Username, StringComparison.Ordinal) ||
                !string.Equals(req.Password, o.Password, StringComparison.Ordinal))
            {
                return Results.Unauthorized();
            }

            var identity = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.Name, o.Username),
                new Claim(ClaimTypes.Role, "user"),
            }, CookieAuthenticationDefaults.AuthenticationScheme);
            await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(identity),
                new AuthenticationProperties { IsPersistent = true, ExpiresUtc = DateTimeOffset.UtcNow.AddDays(o.CookieDurationDays) });
            return Results.Ok(new { user = o.Username });
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
