using LinkHarvester.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LinkHarvester.Synology;

public static class SynologyServiceCollectionExtensions
{
    public static IServiceCollection AddHarvesterSynology(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<SynologyOptions>(config.GetSection("Synology"));
        services.AddHttpClient(nameof(DownloadStationClient));
        services.AddSingleton<IDownloadStationClient>(sp =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            var http = factory.CreateClient(nameof(DownloadStationClient));
            return new DownloadStationClient(http,
                sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<SynologyOptions>>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<DownloadStationClient>>());
        });
        return services;
    }
}
