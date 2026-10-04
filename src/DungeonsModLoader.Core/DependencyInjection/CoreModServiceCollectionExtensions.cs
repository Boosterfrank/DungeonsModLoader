using System.Runtime.Versioning;
using DungeonsModLoader.Core.Install;
using DungeonsModLoader.Core.Mods;
using DungeonsModLoader.Core.Permissions;
using Microsoft.Extensions.DependencyInjection;

namespace DungeonsModLoader.Core.DependencyInjection;

/// <summary>Manifest, mod store and permission-fixer registrations.</summary>
public static class CoreModServiceCollectionExtensions
{
    /// <summary>Windows-only because the permission fixer elevates through UAC and addresses the user by Windows SID.</summary>
    [SupportedOSPlatform("windows")]
    public static IServiceCollection AddCoreMods(this IServiceCollection services)
    {
        services.AddSingleton<IManifestStore, JsonManifestStore>();
        services.AddSingleton<IModService, ModService>();
        services.AddSingleton<IPermissionFixer, PermissionFixer>();
        services.AddSingleton<IModInstaller, ModInstaller>();
        return services;
    }
}
