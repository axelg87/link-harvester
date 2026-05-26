using LinkHarvester.Core;
using LinkHarvester.Resolution.HealthCheck;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LinkHarvester.Resolution;

public static class ResolutionServiceCollectionExtensions
{
    public static IServiceCollection AddHarvesterResolution(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<ResolverOptions>(config.GetSection("Resolver"));
        services.Configure<CapSolverOptions>(config.GetSection("CapSolver"));

        services.AddSingleton<ICapSolverBudget, CapSolverBudget>();
        services.AddHttpClient(NamedClients.DlProtect, c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddHttpClient<CapSolverClient>(c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddSingleton<ILinkResolver, DlProtectResolver>();

        services.AddHttpClient(NamedClients.LinkHealth, c =>
        {
            c.Timeout = TimeSpan.FromSeconds(15);
            c.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/121.0 Safari/537.36");
            c.DefaultRequestHeaders.AcceptLanguage.ParseAdd("fr-FR,fr;q=0.9,en;q=0.8");
        });
        services.AddSingleton<ILinkHealthService, LinkHealthService>();
        return services;
    }
}
