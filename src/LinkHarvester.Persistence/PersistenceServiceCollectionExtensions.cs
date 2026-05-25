using LinkHarvester.Persistence.Catalog;
using Microsoft.Extensions.DependencyInjection;

namespace LinkHarvester.Persistence;

public static class PersistenceServiceCollectionExtensions
{
    public static IServiceCollection AddCatalogIngestion(this IServiceCollection services)
    {
        services.AddSingleton<CatalogIngestor>();
        services.AddSingleton<CatalogIngestionRunner>();
        services.AddSingleton<ICatalogIngestionStatus>(sp => sp.GetRequiredService<CatalogIngestionRunner>());
        return services;
    }
}
