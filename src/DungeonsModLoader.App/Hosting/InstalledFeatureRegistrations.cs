using DungeonsModLoader.App.Services;
using Microsoft.Extensions.DependencyInjection;

namespace DungeonsModLoader.App.Hosting;

/// <summary>
/// Registrations for the Installed page, local mod installation and the Play / game-running feature. The view
/// models themselves are registered in <see cref="ServiceCollectionExtensions.AddViewModels"/>; the services they
/// use (mod store, installer, game context, process monitor, launcher, permission fixer, settings) come from the
/// Core registrations, and <c>IDialogService</c> from the setup feature.
/// </summary>
public static class InstalledFeatureRegistrations
{
    public static IServiceCollection AddInstalledFeature(this IServiceCollection services)
    {
        // One coordinator for the whole app: drag & drop on the shell and "Install from file..." share it.
        services.AddSingleton<IInstallCoordinator, InstallCoordinator>();
        return services;
    }
}
