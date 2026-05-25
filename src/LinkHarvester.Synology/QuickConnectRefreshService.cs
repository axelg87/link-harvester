using LinkHarvester.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LinkHarvester.Synology;

public sealed class QuickConnectRefreshService : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);
    private readonly ISettingsService _settings;
    private readonly IQuickConnectEndpointService _endpoints;
    private readonly ILogger<QuickConnectRefreshService> _log;

    public QuickConnectRefreshService(
        ISettingsService settings,
        IQuickConnectEndpointService endpoints,
        ILogger<QuickConnectRefreshService> log)
    {
        _settings = settings;
        _endpoints = endpoints;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(InitialDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var current = _settings.Current;
                if (current.SynologyConnectionMode == SynologyConnectionMode.QuickConnect)
                {
                    await _endpoints.EnsureResolvedBaseUrlAsync(forceRefresh: false, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Daily QuickConnect endpoint refresh failed.");
            }

            try
            {
                await Task.Delay(CheckInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
