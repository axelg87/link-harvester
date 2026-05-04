using LinkHarvester.Core;
using LinkHarvester.Sources.Zt;
using Microsoft.Extensions.DependencyInjection;

namespace LinkHarvester.Sources;

public static class NamedClients
{
    public const string Zt = "zt";
}

public static class SourcesServiceCollectionExtensions
{
    public static IServiceCollection AddHarvesterSources(this IServiceCollection services)
    {
        services.AddSingleton<IQualityScorer, QualityScorer>();
        services.AddSingleton<ITitleNormalizer, TitleNormalizer>();
        services.AddSingleton<ZtHomepageParser>();
        services.AddSingleton<ZtTitleParser>();
        services.AddSingleton<ZtArticleParser>();

        services.AddHttpClient(NamedClients.Zt, c =>
        {
            c.Timeout = TimeSpan.FromSeconds(30);
            c.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36");
            c.DefaultRequestHeaders.AcceptLanguage.ParseAdd("fr-FR,fr;q=0.9,en;q=0.8");
        });

        services.AddSingleton<IFeedSource, ZtFeedSource>();
        return services;
    }
}
