using LinkHarvester.Api.Auth;
using LinkHarvester.Api.Endpoints;
using LinkHarvester.Core;
using LinkHarvester.Persistence;
using LinkHarvester.Resolution;
using LinkHarvester.Sources;
using LinkHarvester.Synology;
using LinkHarvester.Worker;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseStaticWebAssets();
builder.Host.UseSerilog((ctx, lc) => lc
    .ReadFrom.Configuration(ctx.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console());

var dbPath = builder.Configuration.GetValue<string>("Persistence:DbPath") ?? "data/linkharvester.db";
var dbDir = Path.GetDirectoryName(Path.GetFullPath(dbPath))!;
Directory.CreateDirectory(dbDir);

// Persist DataProtection keys so encrypted settings survive container restarts.
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dbDir, "dp-keys")))
    .SetApplicationName("LinkHarvester");

builder.Services.AddPooledDbContextFactory<HarvesterDbContext>(opts =>
    opts.UseSqlite($"Data Source={dbPath}"));
builder.Services.AddScoped<HarvesterDbContext>(sp =>
    sp.GetRequiredService<IDbContextFactory<HarvesterDbContext>>().CreateDbContext());

builder.Services.AddSingleton<ISettingsService, SettingsService>();

builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = "harvester.auth";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        o.SlidingExpiration = true;
        o.LoginPath = "/login";
        o.Events.OnRedirectToLogin = ctx =>
        {
            if (ctx.Request.Path.StartsWithSegments("/api"))
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            }
            ctx.Response.Redirect(ctx.RedirectUri);
            return Task.CompletedTask;
        };
    });

builder.Services.AddAuthorization();
builder.Services.AddEndpointsApiExplorer();

builder.Services.AddHarvesterSources();
builder.Services.AddHarvesterResolution(builder.Configuration);
builder.Services.AddHarvesterSynology();
builder.Services.AddHarvesterWorker(builder.Configuration);

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<HarvesterDbContext>();
    db.Database.Migrate();
    var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
    await settings.LoadAsync(CancellationToken.None);
}

app.UseSerilogRequestLogging();
app.UseStaticFiles();
app.UseBlazorFrameworkFiles();

app.UseAuthentication();
app.UseAuthorization();

app.MapAuthEndpoints();
app.MapHarvesterEndpoints();
app.MapSettingsEndpoints();

app.MapFallbackToFile("index.html");

app.Run();
