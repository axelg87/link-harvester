using LinkHarvester.Worker.Backfill;
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
        services.AddHostedService<ScanScheduler>();
        return services;
    }
}
