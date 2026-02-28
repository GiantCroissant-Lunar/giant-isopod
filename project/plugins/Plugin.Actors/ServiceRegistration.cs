using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GiantIsopod.Plugin.Actors;

/// <summary>
/// Registers plugin services into the DI container.
/// </summary>
public static class ServiceRegistration
{
    public static IServiceCollection AddGiantIsopodPlugins(
        this IServiceCollection services, AgentWorldConfig config)
    {
        services.AddSingleton(config);
        services.AddSingleton<AgentWorldSystem>();
        return services;
    }
}
