using Microsoft.Extensions.DependencyInjection;

namespace DungeonsModLoader.Core.DependencyInjection;

/// <summary>Game detection, launching and process monitoring registrations.</summary>
public static class CoreGameServiceCollectionExtensions
{
    public static IServiceCollection AddCoreGame(this IServiceCollection services)
    {
        // Filled in by the game-detection work: IGameLocator, IGameLauncher, IGameProcessMonitor.
        return services;
    }
}
