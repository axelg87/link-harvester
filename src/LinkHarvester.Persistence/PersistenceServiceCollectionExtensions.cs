using LinkHarvester.Persistence.Cards;
using LinkHarvester.Persistence.Catalog;
using Microsoft.Extensions.DependencyInjection;

namespace LinkHarvester.Persistence;

public static class PersistenceServiceCollectionExtensions
{
    public static IServiceCollection AddCatalogIngestion(this IServiceCollection services)
    {
        services.AddSingleton<CatalogIngestor>();
        services.AddSingleton<HarvesterCatalogPromoter>();
        services.AddSingleton<FollowingDetectionService>();
        services.AddSingleton<FollowingService>();
        services.AddSingleton<CatalogIngestionRunner>();
        services.AddSingleton<ICatalogIngestionStatus>(sp => sp.GetRequiredService<CatalogIngestionRunner>());
        return services;
    }

    public static IServiceCollection AddCardReadModel(this IServiceCollection services)
    {
        services.AddSingleton<ICardKeeper, CardKeeper>();
        services.AddSingleton<CardSyncState>();
        services.AddSingleton<CardBackfillService>();
        return services;
    }
}
