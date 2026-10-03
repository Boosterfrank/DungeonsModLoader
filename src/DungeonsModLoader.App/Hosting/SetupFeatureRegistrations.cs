using Microsoft.Extensions.DependencyInjection;

namespace DungeonsModLoader.App.Hosting;

/// <summary>Registrations for first-run setup, dialogs and the Settings page (filled in by the setup work).</summary>
public static class SetupFeatureRegistrations
{
    public static IServiceCollection AddSetupFeature(this IServiceCollection services)
    {
        // IDialogService -> DialogService, SetupWindow, ...
        return services;
    }
}
