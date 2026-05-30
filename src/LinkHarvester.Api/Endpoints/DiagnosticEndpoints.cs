using LinkHarvester.Persistence;
using LinkHarvester.Persistence.Cards;
using Microsoft.EntityFrameworkCore;

namespace LinkHarvester.Api.Endpoints;

/// <summary>
/// Auth-protected diagnostic endpoints. Currently scoped to dl-protect:
/// runs the same GET + unlock POST flow as <c>DlProtectResolver</c> and
/// returns the raw response body so the operator can inspect what the
/// site is actually serving from outside the resolver's regex pipeline.
///
/// Why an endpoint instead of just better logs: the sandbox the assistant
/// runs in blocks outbound to dl-protect.link, and logs truncate. This
/// endpoint lets us curl `link-harvester.fly.dev` (which CAN reach the
/// site) and pipe the full body back to a developer terminal.
///
/// Strictly read-only. No DB writes, no DSM calls. Auth-required.
/// </summary>
public static class DiagnosticEndpoints
{
    public static IEndpointRouteBuilder MapDiagnosticEndpoints(this IEndpointRouteBuilder routes)
    {
        var grp = routes.MapGroup("/api/diag").RequireAuthorization();

        // ── Card read-model status ─────────────────────────────────────────
        // Surface enough state to know whether the v42+ read-model deploy
        // is healthy: backfill timestamp, in-process readiness flag, row
        // counts per card table. The reader needs IsReady=true to take
        // the fast path; if it's false days after deploy, this endpoint
        // tells us why (probably "CardsBackfilledAt is null and the
        // background backfill threw").
        grp.MapGet("/cards/status", async (HarvesterDbContext db, CardSyncState state, CancellationToken ct) =>
        {
            var settings = await db.AppSettings.AsNoTracking().FirstOrDefaultAsync(ct);
            var inboxCount = await db.InboxCards.AsNoTracking().CountAsync(ct);
            var inboxVisible = await db.InboxCards.AsNoTracking().CountAsync(c => c.Visible, ct);
            var catalogCount = await db.CatalogCards.AsNoTracking().CountAsync(ct);
            var genreCount = await db.CatalogCardGenres.AsNoTracking().CountAsync(ct);
            var facetCount = await db.CatalogCardLinkFacets.AsNoTracking().CountAsync(ct);

            var baseTitleCount = await db.Titles.AsNoTracking().CountAsync(ct);
            var baseCatalogTitleCount = await db.CatalogTitles.AsNoTracking().CountAsync(ct);

            return Results.Ok(new
            {
                isReady = state.IsReady,
                cardsBackfilledAt = settings?.CardsBackfilledAt,
                rows = new
                {
                    inboxCards = inboxCount,
                    inboxCardsVisible = inboxVisible,
                    catalogCards = catalogCount,
                    catalogCardGenres = genreCount,
                    catalogCardLinkFacets = facetCount,
                },
                baseRows = new
                {
                    titles = baseTitleCount,
                    catalogTitles = baseCatalogTitleCount,
                }
            });
        });

        // Trigger a synchronous rebuild from base. Long-running on a large
        // catalog (~minutes); the response only returns when done so the
        // caller learns whether it actually succeeded. After it returns,
        // the in-process IsReady flag is true.
        //
        // ?phase=inbox|catalog|all (default all). The "inbox" mode runs in
        // ~20-30s and fits inside the Fly LB request window, so the
        // CardsBackfilledAt stamp INSERT after the loop reliably lands and
        // survives a machine restart. The full rebuild often runs longer
        // than the LB will hold the connection, which is why the stamp
        // sometimes failed to persist after a successful catalog rebuild.
        grp.MapPost("/cards/rebuild", async (
            HarvesterDbContext db, ICardKeeper keeper, CardSyncState state,
            ILoggerFactory loggers, string? phase, CancellationToken ct) =>
        {
            var log = loggers.CreateLogger("CardsRebuild");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var mode = (phase ?? "all").ToLowerInvariant();
            log.LogInformation("manual card read-model rebuild starting (phase={Phase})", mode);
            try
            {
                switch (mode)
                {
                    case "inbox":
                        await keeper.RebuildInboxAsync(db, ct);
                        break;
                    case "catalog":
                        await keeper.RebuildCatalogAsync(db, ct);
                        break;
                    case "all":
                        await keeper.RebuildAllAsync(db, ct);
                        break;
                    default:
                        return Results.BadRequest(new { error = $"unknown phase '{phase}'; expected inbox|catalog|all" });
                }

                // Stamp + MarkReady with CancellationToken.None: the request's
                // ct may already be tripped if the LB closed the connection
                // mid-rebuild, but the server-side work completed and the
                // stamp needs to land regardless so IsReady survives a
                // machine restart. The write is a single-row UPDATE — <50ms
                // — well inside any sensible deadline.
                var settings = await db.AppSettings.FirstOrDefaultAsync(CancellationToken.None);
                if (settings is not null)
                {
                    settings.CardsBackfilledAt = DateTimeOffset.UtcNow;
                    settings.UpdatedAt = DateTimeOffset.UtcNow;
                    await db.SaveChangesAsync(CancellationToken.None);
                }
                state.MarkReady();
                log.LogInformation("manual card read-model rebuild succeeded in {Elapsed} (phase={Phase})", sw.Elapsed, mode);
                return Results.Ok(new { ok = true, phase = mode, elapsed = sw.Elapsed.ToString() });
            }
            catch (Exception ex)
            {
                log.LogError(ex, "manual card read-model rebuild failed after {Elapsed} (phase={Phase})", sw.Elapsed, mode);
                return Results.Problem(detail: ex.ToString(), statusCode: 500);
            }
        });

        grp.MapGet("/dlprotect/fetch", async (string url, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(url))
                return Results.BadRequest(new { error = "url query param required" });
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u)
                || !string.Equals(u.Host, "dl-protect.link", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "url must be on host dl-protect.link" });

            var cookies = new System.Net.CookieContainer();
            using var handler = new HttpClientHandler
            {
                CookieContainer = cookies,
                AllowAutoRedirect = true,
                UseCookies = true
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/121.0 Safari/537.36");
            http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("fr-FR,fr;q=0.9,en;q=0.8");
            http.DefaultRequestHeaders.Add("Referer", "https://www.zone-telechargement.news/");

            string getBody;
            int getStatus;
            using (var getResp = await http.GetAsync(url, ct))
            {
                getStatus = (int)getResp.StatusCode;
                getBody = await getResp.Content.ReadAsStringAsync(ct);
            }

            var form = new List<KeyValuePair<string, string>>
            {
                new("subform", "unlock"),
                new("cf-turnstile-response", "invalid")
            };
            using var postContent = new FormUrlEncodedContent(form);
            string postBody;
            int postStatus;
            using (var postResp = await http.PostAsync(url, postContent, ct))
            {
                postStatus = (int)postResp.StatusCode;
                postBody = await postResp.Content.ReadAsStringAsync(ct);
            }

            return Results.Ok(new
            {
                url,
                get = new { status = getStatus, bodyLen = getBody.Length, body = getBody },
                post = new { status = postStatus, bodyLen = postBody.Length, body = postBody }
            });
        });

        return routes;
    }
}
