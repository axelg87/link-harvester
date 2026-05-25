using LinkHarvester.Core;
using Microsoft.Extensions.Logging;

namespace LinkHarvester.Synology;

public interface IQuickConnectEndpointService
{
    Task<QuickConnectResolution> RefreshAsync(string? quickConnectIdOverride, CancellationToken ct);
    Task<string> EnsureResolvedBaseUrlAsync(bool forceRefresh, CancellationToken ct);
}

public sealed class QuickConnectEndpointService : IQuickConnectEndpointService
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromDays(1);
    private readonly ISettingsService _settings;
    private readonly IQuickConnectResolver _resolver;
    private readonly ILogger<QuickConnectEndpointService> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public QuickConnectEndpointService(
        ISettingsService settings,
        IQuickConnectResolver resolver,
        ILogger<QuickConnectEndpointService> log)
    {
        _settings = settings;
        _resolver = resolver;
        _log = log;
    }

    public async Task<string> EnsureResolvedBaseUrlAsync(bool forceRefresh, CancellationToken ct)
    {
        var current = _settings.Current;
        if (current.SynologyConnectionMode != SynologyConnectionMode.QuickConnect)
            return current.SynologyBaseUrl;

        if (!forceRefresh
            && !string.IsNullOrWhiteSpace(current.SynologyResolvedBaseUrl)
            && current.SynologyResolvedAt is { } resolvedAt
            && DateTimeOffset.UtcNow - resolvedAt < RefreshInterval)
        {
            return current.SynologyResolvedBaseUrl;
        }

        var refreshed = await RefreshAsync(null, ct);
        return refreshed.BaseUrl;
    }

    public async Task<QuickConnectResolution> RefreshAsync(string? quickConnectIdOverride, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var current = _settings.Current;
            var quickConnectId = string.IsNullOrWhiteSpace(quickConnectIdOverride)
                ? current.SynologyQuickConnectId
                : quickConnectIdOverride.Trim();
            if (string.IsNullOrWhiteSpace(quickConnectId))
                throw new QuickConnectResolveException("QuickConnect ID is not configured.");

            var resolution = await _resolver.ResolveAsync(quickConnectId, ct);
            await _settings.UpdateAsync(current with
            {
                SynologyConnectionMode = SynologyConnectionMode.QuickConnect,
                SynologyQuickConnectId = quickConnectId,
                SynologyResolvedBaseUrl = resolution.BaseUrl,
                SynologyResolvedAt = resolution.ResolvedAt
            }, ct);
            _log.LogInformation(
                "Resolved Synology QuickConnect ID {QuickConnectId} to {BaseUrl}.",
                quickConnectId,
                resolution.BaseUrl);
            return resolution;
        }
        finally
        {
            _gate.Release();
        }
    }
}
