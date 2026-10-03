using Microsoft.Extensions.DependencyInjection;

namespace DungeonsModLoader.Core.DependencyInjection;

/// <summary>Manifest, mod store and permission-fixer registrations.</summary>
public static class CoreModServiceCollectionExtensions
{
    public static IServiceCollection AddCoreMods(this IServiceCollection services)
    {
        // Filled in by the mod-store work: IManifestStore, IModService, IPermissionFixer.
        return services;
    }
}
