using LinkHarvester.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LinkHarvester.Worker;

/// <summary>
/// Background hosted service that periodically asks each registered IFeedSource
/// to scan. Also exposes a "kick now" channel for the API endpoint.
/// </summary>
public sealed class ScanScheduler : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly IEnumerable<IFeedSource> _sources;
    private readonly ISettingsService _settings;
    private readonly ILogger<ScanScheduler> _log;
    private readonly ScanTrigger _trigger;

    public ScanScheduler(IServiceProvider services,
                         IEnumerable<IFeedSource> sources,
                         ISettingsService settings,
                         ScanTrigger trigger,
                         ILogger<ScanScheduler> log)
    {
        _services = services;
        _sources = sources;
        _settings = settings;
        _log = log;
        _trigger = trigger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_settings.Current.ScanOnStartup)
        {
            _trigger.RequestScan();
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var minutes = Math.Max(1, _settings.Current.ScanIntervalMinutes);
            var interval = TimeSpan.FromMinutes(minutes);
            try
            {
                await _trigger.WaitOrIntervalAsync(interval, stoppingToken);
                if (stoppingToken.IsCancellationRequested) break;
                await ScanAllAsync(stoppingToken);
            }
            catch (OperationCanceledException) { /* shutdown */ }
            catch (Exception ex)
            {
                _log.LogError(ex, "Scan loop crashed");
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            }
        }
    }

    private async Task ScanAllAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var pipeline = scope.ServiceProvider.GetRequiredService<ScanPipeline>();
        foreach (var src in _sources)
        {
            ct.ThrowIfCancellationRequested();
            _log.LogInformation("Starting scan for source {Source}", src.Id);
            try
            {
                var run = await pipeline.RunAsync(src.Id, ct);
                _log.LogInformation("Scan {Id} for {Src}: discovered={D} new={N} failed={F}",
                    run.Id, src.Id, run.Discovered, run.NewArticles, run.Failed);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Scan failed for {Source}", src.Id);
            }
        }
    }
}

public sealed class ScanTrigger
{
    private readonly SemaphoreSlim _semaphore = new(0, 1);

    public void RequestScan()
    {
        try { _semaphore.Release(); }
        catch (SemaphoreFullException) { /* already pending */ }
    }

    public async Task WaitOrIntervalAsync(TimeSpan interval, CancellationToken ct)
    {
        await _semaphore.WaitAsync(interval, ct);
    }
}
