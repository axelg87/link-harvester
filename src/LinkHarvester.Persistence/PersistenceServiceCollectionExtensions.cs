using LinkHarvester.Persistence.Catalog;
using LinkHarvester.Persistence.Maintenance;
using Microsoft.Extensions.DependencyInjection;

namespace LinkHarvester.Persistence;

public static class PersistenceServiceCollectionExtensions
{
    public static IServiceCollection AddCatalogIngestion(this IServiceCollection services)
    {
        services.AddSingleton<CatalogIngestor>();
        services.AddSingleton<HarvesterCatalogPromoter>();
        services.AddSingleton<CatalogIngestionRunner>();
        services.AddSingleton<ICatalogIngestionStatus>(sp => sp.GetRequiredService<CatalogIngestionRunner>());
        // Background repair for DateTimeOffset columns the Hydracker ingestor
        // wrote in the wrong format. Runs once, idempotent on subsequent boots.
        services.AddHostedService<DateTimeOffsetRepairService>();
        return services;
    }
}
