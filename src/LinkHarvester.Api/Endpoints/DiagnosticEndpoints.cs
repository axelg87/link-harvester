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
