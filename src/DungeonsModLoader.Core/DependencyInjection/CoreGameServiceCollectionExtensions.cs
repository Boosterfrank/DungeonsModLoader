using DungeonsModLoader.Core.Game;
using DungeonsModLoader.Core.Game.Steam;
using DungeonsModLoader.Core.Game.Xbox;
using Microsoft.Extensions.DependencyInjection;

namespace DungeonsModLoader.Core.DependencyInjection;

/// <summary>Game detection, launching and process monitoring registrations.</summary>
public static class CoreGameServiceCollectionExtensions
{
    public static IServiceCollection AddCoreGame(this IServiceCollection services)
    {
        // Platform lookups (registry). The locators themselves are plain file/XML readers.
        services.AddSingleton<ISteamRegistry>(_ => OperatingSystem.IsWindows() ? new WindowsSteamRegistry() : new FixedSteamRegistry(null));
        services.AddSingleton<IXboxPackageRepository>(_ => OperatingSystem.IsWindows() ? new WindowsXboxPackageRepository() : new FixedXboxPackageRepository());

        // Per-source locators, exposed as IGameSourceLocator in Steam-then-Xbox order for the composite.
        services.AddSingleton<SteamGameLocator>();
        services.AddSingleton<XboxGameLocator>();
        services.AddSingleton<IGameSourceLocator>(provider => provider.GetRequiredService<SteamGameLocator>());
        services.AddSingleton<IGameSourceLocator>(provider => provider.GetRequiredService<XboxGameLocator>());

        services.AddSingleton<IGameLocator, CompositeGameLocator>();
        services.AddSingleton<IGameLauncher, GameLauncher>();
        services.AddSingleton<IGameProcessMonitor, GameProcessMonitor>();
        return services;
    }
}
