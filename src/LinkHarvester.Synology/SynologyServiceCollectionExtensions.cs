using LinkHarvester.Core;
using Microsoft.Extensions.DependencyInjection;

namespace LinkHarvester.Synology;

public static class SynologyServiceCollectionExtensions
{
    public static IServiceCollection AddHarvesterSynology(this IServiceCollection services)
    {
        services.AddHttpClient(nameof(DownloadStationClient));
        services.AddHttpClient(nameof(QuickConnectResolver), c =>
        {
            c.Timeout = TimeSpan.FromSeconds(20);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("LinkHarvester/1.0");
        });
        services.AddSingleton<IQuickConnectResolver>(sp =>
        {
            var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(QuickConnectResolver));
            return new QuickConnectResolver(
                http,
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<QuickConnectResolver>>());
        });
        services.AddSingleton<IQuickConnectEndpointService, QuickConnectEndpointService>();
        services.AddHostedService<QuickConnectRefreshService>();
        services.AddSingleton<IDownloadStationClient>(sp =>
        {
            var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(DownloadStationClient));
            return new DownloadStationClient(
                http,
                sp.GetRequiredService<ISettingsService>(),
                sp.GetRequiredService<IQuickConnectEndpointService>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<DownloadStationClient>>());
        });
        return services;
    }
}
