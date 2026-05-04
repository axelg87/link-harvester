using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LinkHarvester.Resolution;

/// <summary>
/// Minimal CapSolver client supporting Cloudflare Turnstile (the captcha used
/// by dl-protect.link). Submits a task and polls until solved.
/// Doc: https://docs.capsolver.com/guide/captcha/Turnstile.html
/// </summary>
public sealed class CapSolverClient
{
    private readonly HttpClient _http;
    private readonly CapSolverOptions _opts;
    private readonly ILogger<CapSolverClient> _log;

    public CapSolverClient(HttpClient http, IOptions<CapSolverOptions> opts, ILogger<CapSolverClient> log)
    {
        _http = http;
        _opts = opts.Value;
        _log = log;
    }

    public bool IsConfigured => _opts.Enabled && !string.IsNullOrWhiteSpace(_opts.ApiKey);

    /// <summary>
    /// Solve a Cloudflare Turnstile widget. Returns null on failure.
    /// </summary>
    public async Task<string?> SolveTurnstileAsync(string siteUrl, string siteKey, CancellationToken ct)
    {
        if (!IsConfigured)
        {
            _log.LogWarning("CapSolver not configured; skipping solve.");
            return null;
        }

        var createReq = new
        {
            clientKey = _opts.ApiKey,
            task = new
            {
                type = "AntiTurnstileTaskProxyless",
                websiteURL = siteUrl,
                websiteKey = siteKey
            }
        };

        var createResp = await _http.PostAsJsonAsync($"{_opts.ApiBaseUrl}/createTask", createReq, ct);
        var create = await createResp.Content.ReadFromJsonAsync<CreateTaskResponse>(cancellationToken: ct);
        if (create is null || create.ErrorId != 0 || string.IsNullOrEmpty(create.TaskId))
        {
            _log.LogError("CapSolver createTask failed: {Error}", create?.ErrorDescription);
            return null;
        }

        for (var i = 0; i < _opts.MaxPollAttempts; i++)
        {
            await Task.Delay(_opts.PollIntervalMs, ct);
            var getReq = new { clientKey = _opts.ApiKey, taskId = create.TaskId };
            var getResp = await _http.PostAsJsonAsync($"{_opts.ApiBaseUrl}/getTaskResult", getReq, ct);
            var get = await getResp.Content.ReadFromJsonAsync<GetTaskResultResponse>(cancellationToken: ct);
            if (get is null) continue;
            if (get.ErrorId != 0)
            {
                _log.LogError("CapSolver getTaskResult error: {Error}", get.ErrorDescription);
                return null;
            }
            if (get.Status == "ready" && get.Solution is not null)
            {
                return get.Solution.Token;
            }
        }

        _log.LogError("CapSolver timed out after {N} poll attempts", _opts.MaxPollAttempts);
        return null;
    }

    private sealed record CreateTaskResponse(
        [property: JsonPropertyName("errorId")] int ErrorId,
        [property: JsonPropertyName("errorDescription")] string? ErrorDescription,
        [property: JsonPropertyName("taskId")] string? TaskId);

    private sealed record GetTaskResultResponse(
        [property: JsonPropertyName("errorId")] int ErrorId,
        [property: JsonPropertyName("errorDescription")] string? ErrorDescription,
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("solution")] TurnstileSolution? Solution);

    private sealed record TurnstileSolution(
        [property: JsonPropertyName("token")] string? Token);
}
