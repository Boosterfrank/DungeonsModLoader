using Microsoft.Extensions.DependencyInjection;

namespace DungeonsModLoader.App.Hosting;

/// <summary>
/// Registrations for the Installed page and the Play / game-running feature. The view models themselves are
/// registered in <see cref="ServiceCollectionExtensions.AddViewModels"/>; the services they use (mod store, game
/// context, process monitor, launcher, permission fixer, settings) come from the Core registrations, and
/// <c>IDialogService</c> from the setup feature. Nothing extra is needed yet.
/// </summary>
public static class InstalledFeatureRegistrations
{
    public static IServiceCollection AddInstalledFeature(this IServiceCollection services)
    {
        return services;
    }
}
