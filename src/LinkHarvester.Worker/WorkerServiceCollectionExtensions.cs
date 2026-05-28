using LinkHarvester.Worker.Backfill;
using LinkHarvester.Worker.Discovery;
using LinkHarvester.Worker.Telegram;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace LinkHarvester.Worker;

public static class WorkerServiceCollectionExtensions
{
    public static IServiceCollection AddHarvesterWorker(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<HarvesterOptions>(config.GetSection("Harvester"));
        services.AddSingleton<ScanTrigger>();
        services.AddScoped<ScanPipeline>();
        services.AddScoped<SubmissionService>();
        services.AddScoped<BackfillService>();
        services.AddScoped<LinkHealthSweepService>();
        services.AddSingleton<BackfillJobRunner>();
        services.AddSingleton<DiscoveryRefreshService>();
        services.AddHostedService(sp => sp.GetRequiredService<DiscoveryRefreshService>());
        services.AddSingleton<TelegramBotService>();
        services.AddHostedService(sp => sp.GetRequiredService<TelegramBotService>());
        services.AddHostedService<TelegramNotificationDispatcher>();
        services.AddHostedService<ScanScheduler>();
        return services;
    }
}
