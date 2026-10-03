using Microsoft.Extensions.DependencyInjection;

namespace DungeonsModLoader.App.Hosting;

/// <summary>Registrations for the Installed page and the Play / game-running feature (filled in by that work).</summary>
public static class InstalledFeatureRegistrations
{
    public static IServiceCollection AddInstalledFeature(this IServiceCollection services)
    {
        return services;
    }
}
