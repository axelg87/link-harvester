using LinkHarvester.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LinkHarvester.Resolution;

public static class ResolutionServiceCollectionExtensions
{
    public static IServiceCollection AddHarvesterResolution(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<ResolverOptions>(config.GetSection("Resolver"));
        services.Configure<CapSolverOptions>(config.GetSection("CapSolver"));

        services.AddSingleton<PlaywrightInstaller>();
        services.AddSingleton<ICapSolverBudget, CapSolverBudget>();
        services.AddHttpClient<CapSolverClient>(c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddSingleton<ILinkResolver, DlProtectResolver>();
        return services;
    }
}
