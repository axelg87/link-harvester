using Microsoft.Extensions.DependencyInjection;

namespace LinkHarvester.Enrichment;

public static class EnrichmentServiceCollectionExtensions
{
    public static IServiceCollection AddCatalogEnrichment(this IServiceCollection services)
    {
        services.AddHttpClient<TmdbClient>(c =>
        {
            c.DefaultRequestHeaders.UserAgent.ParseAdd("LinkHarvester/1.0 (+https://linkharvester.fly.dev)");
        });
        services.AddSingleton<TmdbStatusTracker>();
        services.AddHostedService<TmdbEnricherService>();
        return services;
    }
}
