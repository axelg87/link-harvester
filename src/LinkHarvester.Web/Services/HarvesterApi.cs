using System.Net;
using System.Net.Http.Json;
using LinkHarvester.Web.Models;

namespace LinkHarvester.Web.Services;

public sealed class HarvesterApi
{
    private readonly HttpClient _http;
    public HarvesterApi(HttpClient http) { _http = http; }

    public async Task<bool> IsAuthenticatedAsync()
    {
        using var resp = await _http.GetAsync("api/auth/me");
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> LoginAsync(string username, string password)
    {
        using var resp = await _http.PostAsJsonAsync("api/auth/login", new { username, password });
        return resp.IsSuccessStatusCode;
    }

    public async Task LogoutAsync()
    {
        using var resp = await _http.PostAsync("api/auth/logout", null);
    }

    public async Task<List<InboxCard>> GetInboxAsync(CancellationToken ct = default)
    {
        return await _http.GetFromJsonAsync<List<InboxCard>>("api/inbox", ct) ?? new();
    }

    public async Task TriggerScanAsync(CancellationToken ct = default)
    {
        using var resp = await _http.PostAsync("api/scan", null, ct);
    }

    public async Task<List<ScanRun>> GetScanHistoryAsync(CancellationToken ct = default)
    {
        return await _http.GetFromJsonAsync<List<ScanRun>>("api/scans", ct) ?? new();
    }

    public async Task SkipArticleAsync(int articleId, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsync($"api/articles/{articleId}/skip", null, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<SendResult> SendArticleAsync(int articleId, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsync($"api/articles/{articleId}/send", null, ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<SendResult>(cancellationToken: ct))!;
    }

    public async Task<BudgetSnapshot> GetBudgetAsync(CancellationToken ct = default)
    {
        return (await _http.GetFromJsonAsync<BudgetSnapshot>("api/budget", ct))!;
    }

    public async Task<Settings> GetSettingsAsync(CancellationToken ct = default)
    {
        return (await _http.GetFromJsonAsync<Settings>("api/settings", ct))!;
    }

    public async Task SaveSettingsAsync(UpdateSettings update, CancellationToken ct = default)
    {
        using var resp = await _http.PutAsJsonAsync("api/settings", update, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<SynologyTestResult> TestSynologyAsync(CancellationToken ct = default)
    {
        using var resp = await _http.PostAsync("api/settings/test-synology", null, ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<SynologyTestResult>(cancellationToken: ct))!;
    }

    public async Task<SendHistoryPage> GetSendHistoryAsync(string? status, string? source, int page = 1, int pageSize = 50, CancellationToken ct = default)
    {
        var qs = new List<string> { $"page={page}", $"pageSize={pageSize}" };
        if (!string.IsNullOrWhiteSpace(status)) qs.Add($"status={Uri.EscapeDataString(status)}");
        if (!string.IsNullOrWhiteSpace(source)) qs.Add($"source={Uri.EscapeDataString(source)}");
        return (await _http.GetFromJsonAsync<SendHistoryPage>("api/sends?" + string.Join('&', qs), ct))!;
    }

    public async Task<SendResendResult> ResendAsync(int id, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsync($"api/sends/{id}/resend", null, ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<SendResendResult>(cancellationToken: ct))!;
    }

    public async Task<QuickConnectResolveResult> ResolveQuickConnectAsync(string? quickConnectId, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync("api/settings/resolve-quickconnect", new { quickConnectId }, ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<QuickConnectResolveResult>(cancellationToken: ct))!;
    }

    // ── Catalog ───────────────────────────────────────────────────────────
    public async Task<CatalogStats> GetCatalogStatsAsync(CancellationToken ct = default)
        => (await _http.GetFromJsonAsync<CatalogStats>("api/catalog/stats", ct))!;

    public async Task<CatalogFacets> GetCatalogFacetsAsync(CancellationToken ct = default)
        => (await _http.GetFromJsonAsync<CatalogFacets>("api/catalog/facets", ct))!;

    public async Task<List<FacetEntry>> GetCatalogGenresAsync(CancellationToken ct = default)
        => (await _http.GetFromJsonAsync<List<FacetEntry>>("api/catalog/genres", ct)) ?? new();

    public async Task<SearchPage> SearchCatalogAsync(CatalogSearchQuery query, CancellationToken ct = default)
    {
        var qs = new List<string>();
        void Add(string k, string? v) { if (!string.IsNullOrEmpty(v)) qs.Add($"{k}={Uri.EscapeDataString(v)}"); }
        Add("q", query.Q);
        Add("category", query.Category);
        if (query.Hosts.Count > 0) Add("hosts", string.Join(',', query.Hosts));
        if (query.Qualities.Count > 0) Add("qualities", string.Join(',', query.Qualities));
        if (query.AudioLangs.Count > 0) Add("audio", string.Join(',', query.AudioLangs));
        if (query.Genres.Count > 0) Add("genres", string.Join(',', query.Genres));
        if (query.YearMin.HasValue) Add("yearMin", query.YearMin.ToString());
        if (query.YearMax.HasValue) Add("yearMax", query.YearMax.ToString());
        if (query.RatingMin.HasValue) Add("ratingMin", query.RatingMin.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (query.RuntimeMin.HasValue) Add("runtimeMin", query.RuntimeMin.ToString());
        if (query.RuntimeMax.HasValue) Add("runtimeMax", query.RuntimeMax.ToString());
        if (query.SizeMinBytes.HasValue) Add("sizeMinBytes", query.SizeMinBytes.ToString());
        if (query.SizeMaxBytes.HasValue) Add("sizeMaxBytes", query.SizeMaxBytes.ToString());
        Add("originalLanguage", query.OriginalLanguage);
        if (query.HasMetadataOnly) Add("hasMetadata", "true");
        Add("sort", query.Sort);
        Add("page", query.Page.ToString());
        Add("pageSize", query.PageSize.ToString());

        var url = "api/catalog/search" + (qs.Count > 0 ? "?" + string.Join('&', qs) : "");
        return (await _http.GetFromJsonAsync<SearchPage>(url, ct))!;
    }

    public async Task<TitleDetail?> GetTitleAsync(int id, CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync($"api/catalog/titles/{id}", ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<TitleDetail>(cancellationToken: ct);
    }

    public async Task<CatalogSendResult> SendCatalogLinksAsync(IEnumerable<int> linkIds, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync("api/catalog/links/send", new { linkIds = linkIds.ToList() }, ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<CatalogSendResult>(cancellationToken: ct))!;
    }

    public async Task<IngestionStatus> GetIngestionStatusAsync(CancellationToken ct = default)
        => (await _http.GetFromJsonAsync<IngestionStatus>("api/catalog/import/status", ct))!;

    public async Task ImportFromUrlAsync(string url, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync("api/catalog/import/from-url", new { url }, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<HttpResponseMessage> UploadCatalogFileAsync(Stream content, string fileName, CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        var streamContent = new StreamContent(content);
        streamContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        form.Add(streamContent, "file", fileName);
        return await _http.PostAsync("api/catalog/import/upload", form, ct);
    }

    public async Task CancelIngestionAsync(CancellationToken ct = default)
    {
        using var resp = await _http.PostAsync("api/catalog/import/cancel", null, ct);
    }

    public async Task<ResetFailedResult> ResetFailedEnrichmentsAsync(bool onlyLockErrors, CancellationToken ct = default)
    {
        var url = $"api/catalog/enrichment/reset-failed?onlyLockErrors={(onlyLockErrors ? "true" : "false")}";
        using var resp = await _http.PostAsync(url, null, ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<ResetFailedResult>(cancellationToken: ct))!;
    }

    // ── Backfill & health sweep ──────────────────────────────────────────
    public async Task<BackfillStatusDto?> StartBackfillAsync(string source, string kind, string fromDateIso, int? startPage, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync("api/backfill/start",
            new { source, kind, fromDate = fromDateIso, startPage }, ct);
        return await GetBackfillStatusAsync(ct);
    }
    public async Task CancelBackfillAsync(CancellationToken ct = default) =>
        (await _http.PostAsync("api/backfill/cancel", null, ct)).EnsureSuccessStatusCode();

    public async Task<BackfillStatusDto?> GetBackfillStatusAsync(CancellationToken ct = default) =>
        await _http.GetFromJsonAsync<BackfillStatusDto>("api/backfill/status", ct);

    public async Task<List<BackfillRunDto>> GetBackfillRunsAsync(CancellationToken ct = default) =>
        (await _http.GetFromJsonAsync<List<BackfillRunDto>>("api/backfill/runs", ct)) ?? new();

    public async Task<SweepStatusDto?> StartSweepAsync(string? hosterFilter, bool resume, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync("api/backfill/sweep/start",
            new { hosterFilter, resume }, ct);
        return await GetSweepStatusAsync(ct);
    }
    public async Task CancelSweepAsync(CancellationToken ct = default) =>
        (await _http.PostAsync("api/backfill/sweep/cancel", null, ct)).EnsureSuccessStatusCode();

    public async Task<SweepStatusDto?> GetSweepStatusAsync(CancellationToken ct = default) =>
        await _http.GetFromJsonAsync<SweepStatusDto>("api/backfill/sweep/status", ct);

    public async Task<List<HealthSweepRunDto>> GetSweepRunsAsync(CancellationToken ct = default) =>
        (await _http.GetFromJsonAsync<List<HealthSweepRunDto>>("api/backfill/sweep/runs", ct)) ?? new();

    public async Task<List<RecentCatalogTitleDto>> GetRecentCatalogAsync(int take = 5, CancellationToken ct = default) =>
        (await _http.GetFromJsonAsync<List<RecentCatalogTitleDto>>($"api/backfill/recent?take={take}", ct)) ?? new();
}

public sealed record BackfillStatusDto(
    bool Running,
    string? SourceId,
    string? Kind,
    DateTimeOffset? FromDate,
    int StartPage,
    int LastCompletedPage,
    int Discovered,
    int Promoted,
    int Skipped,
    DateTimeOffset? StartedAt,
    string? Error);

public sealed record BackfillRunDto(
    int Id, string SourceId, string Kind, DateTimeOffset FromDate,
    int StartPage, int LastCompletedPage,
    int Discovered, int Promoted, int Skipped,
    string Status, string? Error,
    DateTimeOffset StartedAt, DateTimeOffset? FinishedAt);

public sealed record SweepStatusDto(
    bool Running,
    string? HosterFilter,
    int Checked, int Alive, int Dead, int Unknown, int HiddenTitles,
    int LastCheckedCatalogLinkId,
    DateTimeOffset? StartedAt,
    string? Error);

public sealed record HealthSweepRunDto(
    int Id, string? HosterFilter, int LastCheckedCatalogLinkId,
    int Checked, int Alive, int Dead, int Unknown, int HiddenTitles,
    string Status, string? Error,
    DateTimeOffset StartedAt, DateTimeOffset? FinishedAt);

public sealed record RecentCatalogTitleDto(
    int Id, string TitleName, string CategoryName, string? TitlePoster,
    int LinkCount, bool IsHidden, string? HiddenReason,
    DateTimeOffset FirstSeenAt, DateTimeOffset LastSeenAt,
    int? MetaYear, string? EnrichmentSource);
