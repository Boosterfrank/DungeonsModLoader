using DungeonsModLoader.Core.Game;
using DungeonsModLoader.Core.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace DungeonsModLoader.Core.DependencyInjection;

/// <summary>Core registrations shared by every feature (settings store, game context).</summary>
public static class CoreServiceCollectionExtensions
{
    public static IServiceCollection AddCoreInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<IGameContext, GameContext>();
        services.AddSingleton<ISettingsStore, JsonSettingsStore>();
        return services;
    }
}
