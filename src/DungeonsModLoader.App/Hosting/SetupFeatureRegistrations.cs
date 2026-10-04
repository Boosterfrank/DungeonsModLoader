using DungeonsModLoader.App.Services;
using DungeonsModLoader.App.ViewModels.Setup;
using DungeonsModLoader.App.Views.Setup;
using Microsoft.Extensions.DependencyInjection;

namespace DungeonsModLoader.App.Hosting;

/// <summary>Registrations for first-run setup, dialogs and the Settings page.</summary>
public static class SetupFeatureRegistrations
{
    public static IServiceCollection AddSetupFeature(this IServiceCollection services)
    {
        // One dialog service for the whole app; the themed dialogs it shows are created per call.
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IModStoreInitializer, ModStoreInitializer>();

        // The wizard runs once per start at most, so window and view model are created fresh when needed.
        services.AddTransient<SetupViewModel>();
        services.AddTransient<SetupWindow>();
        return services;
    }
}
