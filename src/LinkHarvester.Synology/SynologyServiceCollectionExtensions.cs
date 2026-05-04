using LinkHarvester.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LinkHarvester.Synology;

public static class SynologyServiceCollectionExtensions
{
    public static IServiceCollection AddHarvesterSynology(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<SynologyOptions>(config.GetSection("Synology"));
        services.AddHttpClient<IDownloadStationClient, DownloadStationClient>();
        return services;
    }
}
