using DungeonsModLoader.App.Services;
using DungeonsModLoader.App.ViewModels;
using DungeonsModLoader.App.ViewModels.Pages;
using DungeonsModLoader.App.Views;
using DungeonsModLoader.App.Views.Dialogs;
using DungeonsModLoader.Core;
using DungeonsModLoader.Core.DependencyInjection;
using DungeonsModLoader.Nexus.Auth;
using DungeonsModLoader.Nexus.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace DungeonsModLoader.App.Hosting;

/// <summary>Every DI registration of the app, grouped by layer.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Name of the shared <see cref="HttpClient"/>: <c>IHttpClientFactory.CreateClient(HttpClientName)</c>.
    /// It carries the app's User-Agent by default (GitHub release checks and the like; Nexus has its own clients).
    /// </summary>
    public const string HttpClientName = AppInfo.DisplayName;

    /// <summary>User-Agent sent by the shared HttpClient: "DungeonsModLoader/0.1.0 (+https://github.com/...)".</summary>
    public static string UserAgent { get; } =
        $"{AppInfo.DisplayName}/{AppInfo.Version} (+https://github.com/{AppInfo.GitHubRepository})";

    /// <summary>Infrastructure and application services (paths, window service, HTTP, Nexus).</summary>
    public static IServiceCollection AddAppServices(this IServiceCollection services, AppPaths paths)
    {
        services.AddSingleton(paths);
        services.AddSingleton<WindowService>();
        services.AddSingleton<IWindowService>(provider => provider.GetRequiredService<WindowService>());
        services.AddSingleton<IUrlOpener>(provider => provider.GetRequiredService<WindowService>());
        services.AddSingleton<INxmProtocolRegistration, NxmProtocolRegistration>();
        services.AddSingleton<ISingleInstanceServer, SingleInstanceServer>();

        services
            .AddCoreInfrastructure()
            .AddCoreGame()
            .AddCoreMods()
            .AddNexus()
            .AddSetupFeature()
            .AddInstalledFeature();

        services.AddHttpClient();
        services.AddHttpClient(HttpClientName, client =>
        {
            client.DefaultRequestHeaders.UserAgent.Clear();
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        });

        return services;
    }

    /// <summary>The shell view model and the four page view models (all singletons: one app, one shell).</summary>
    public static IServiceCollection AddViewModels(this IServiceCollection services)
    {
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<InstalledViewModel>();
        services.AddSingleton<BrowseViewModel>();
        services.AddSingleton<ProfilesViewModel>();
        services.AddSingleton<SettingsViewModel>();
        return services;
    }

    /// <summary>Windows. The main window is a singleton; secondary windows are created per use.</summary>
    public static IServiceCollection AddViews(this IServiceCollection services)
    {
        services.AddSingleton<MainWindow>();
        services.AddTransient<SwatchWindow>();
        services.AddTransient<CrashDialog>();
        return services;
    }

    /// <summary>
    /// Replaces the default console lifetime: in a WPF process the <see cref="System.Windows.Application"/>
    /// decides when to start and stop, so the host must not hook console signals or ProcessExit.
    /// </summary>
    public static IServiceCollection UseWpfLifetime(this IServiceCollection services)
    {
        services.Replace(ServiceDescriptor.Singleton<IHostLifetime, WpfHostLifetime>());
        return services;
    }
}
