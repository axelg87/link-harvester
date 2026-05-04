using LinkHarvester.Core;
using Microsoft.Extensions.DependencyInjection;

namespace LinkHarvester.Synology;

public static class SynologyServiceCollectionExtensions
{
    public static IServiceCollection AddHarvesterSynology(this IServiceCollection services)
    {
        services.AddHttpClient(nameof(DownloadStationClient));
        services.AddSingleton<IDownloadStationClient>(sp =>
        {
            var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(DownloadStationClient));
            return new DownloadStationClient(
                http,
                sp.GetRequiredService<ISettingsService>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<DownloadStationClient>>());
        });
        return services;
    }
}
