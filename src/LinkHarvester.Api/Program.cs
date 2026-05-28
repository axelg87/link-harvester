using LinkHarvester.Api.Auth;
using LinkHarvester.Api.Caching;
using LinkHarvester.Api.Endpoints;
using LinkHarvester.Api.Health;
using LinkHarvester.Api.Maintenance;
using LinkHarvester.Core;
using LinkHarvester.Enrichment;
using LinkHarvester.Persistence;
using LinkHarvester.Persistence.Catalog;
using LinkHarvester.Resolution;
using LinkHarvester.Sources;
using LinkHarvester.Synology;
using LinkHarvester.Worker;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Serilog.Events;

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
builder.Services.AddCatalogIngestion();
builder.Services.AddCatalogEnrichment();
builder.Services.AddSingleton<HealthCheckService>();

// Catalog aggregates cache. /facets and /genres each cost O(rows) — links
// table is 2.3M+, genres scan reads every metadata GenresJson — but the
// answer barely changes between catalog page loads, so cache it.
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<CatalogAggregatesCache>();

// Multi-GB uploads for the catalog dump.
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = long.MaxValue;
    o.ValueLengthLimit = int.MaxValue;
});
builder.WebHost.ConfigureKestrel(opts => opts.Limits.MaxRequestBodySize = null);

// Named HttpClient used by /api/catalog/import/from-url.
builder.Services.AddHttpClient("import", c =>
{
    c.Timeout = TimeSpan.FromHours(2);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("LinkHarvester/1.0");
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<HarvesterDbContext>();
    db.Database.Migrate();
    CatalogFts.EnsureCreated(db);

    var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
    await settings.LoadAsync(CancellationToken.None);

    // BUG-2 healing: any CatalogTitleMetadata rows in the 'failed' bucket
    // whose LastError reads as SQLite write contention or a 30-second
    // command timeout are residue from past concurrent ingest+enrich runs.
    // Reset them to 'pending' so the enricher picks them up on its next
    // batch. Pattern set lives in EnrichmentMaintenance so the manual
    // reset endpoint, the /api/catalog/stats counter, and this auto-heal
    // share one source of truth.
    var startupLog = scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
        .CreateLogger("Startup");
    var promoter = scope.ServiceProvider.GetRequiredService<HarvesterCatalogPromoter>();
    var promotedCount = await promoter.BackfillUnpromotedAsync(CancellationToken.None);
    if (promotedCount > 0)
    {
        startupLog.LogInformation("promoted {Count} existing ZT article(s) into catalog", promotedCount);
    }

    var resetCount = await EnrichmentMaintenance.ResetTransientFailedAsync(db, CancellationToken.None);
    if (resetCount > 0)
    {
        startupLog.LogInformation("reset {Count} transient enrichment failures (SQLite contention residue)", resetCount);
    }

    // Sweep orphaned catalog import runs. A row stuck in 'running' state
    // after process restart can only mean we crashed or were killed mid-
    // ingest — the in-memory CatalogIngestionRunner is gone, so the row
    // is unrecoverable. Marking it 'failed' unblocks the import-status UI
    // and lets a new import start without the 'another ingestion is
    // already running' guard tripping. FinishedAt stays NULL: we don't
    // actually know when the orphan died, and the column is nullable for
    // exactly this reason.
    var orphanedCount = await db.Database.ExecuteSqlRawAsync(@"
        UPDATE CatalogImportRuns
        SET Status = 'failed',
            Notes = COALESCE(Notes, '') || ' [orphaned by app restart]'
        WHERE Status = 'running';");
    if (orphanedCount > 0)
    {
        startupLog.LogWarning("marked {Count} orphaned catalog import run(s) as failed (app restarted mid-ingest)", orphanedCount);
    }
}


app.UseSerilogRequestLogging(opts =>
{
    // Don't drown the application log with Fly's 30-second health probes.
    opts.GetLevel = (httpCtx, _, ex) =>
    {
        if (ex is not null) return LogEventLevel.Error;
        if (httpCtx.Request.Path.StartsWithSegments("/healthz"))
            return LogEventLevel.Verbose;
        return LogEventLevel.Information;
    };
});
app.UseStaticFiles();
app.UseBlazorFrameworkFiles();

app.UseAuthentication();
app.UseAuthorization();

app.MapHealthEndpoints();
app.MapAuthEndpoints();
app.MapHarvesterEndpoints();
app.MapSettingsEndpoints();
app.MapCatalogEndpoints();
app.MapCatalogImportEndpoints();
app.MapBackfillEndpoints();
app.MapSendHistoryEndpoints();
app.MapFollowingEndpoints();
app.MapDiscoveryEndpoints();

app.MapFallbackToFile("index.html");

app.Run();
