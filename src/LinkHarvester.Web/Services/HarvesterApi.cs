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
}
