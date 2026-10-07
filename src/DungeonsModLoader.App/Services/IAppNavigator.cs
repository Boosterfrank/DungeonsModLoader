using DungeonsModLoader.App.ViewModels;
using DungeonsModLoader.App.ViewModels.Pages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DungeonsModLoader.App.Services;

/// <summary>
/// Cross-page navigation for code that is not the shell: a toast's "Show" button, a dependency hint on the
/// Installed page that opens the required mod on the Browse page, a requirement link in a mod's detail. Must be
/// called on the UI thread.
/// </summary>
public interface IAppNavigator
{
    /// <summary>Shows the Installed page, optionally with a filter chip selected.</summary>
    void ShowInstalled(ModFilter? filter = null);

    void ShowBrowse();

    void ShowSettings();

    /// <summary>Shows the Browse page and opens the detail of the mod with that Nexus id (fetched when not in the list).</summary>
    Task ShowNexusModAsync(long nexusModId);
}

/// <summary>
/// <see cref="IAppNavigator"/> over the shell view model. The shell depends on the pages and the pages depend on
/// this, so the shell is resolved lazily (it is a singleton; the first call happens after the window exists).
/// </summary>
public sealed class AppNavigator : IAppNavigator
{
    private readonly IServiceProvider _services;
    private readonly ILogger<AppNavigator> _logger;

    public AppNavigator(IServiceProvider services, ILogger<AppNavigator> logger)
    {
        _services = services;
        _logger = logger;
    }

    private MainViewModel Shell => _services.GetRequiredService<MainViewModel>();

    public void ShowInstalled(ModFilter? filter = null)
    {
        var shell = Shell;
        if (filter is { } f)
        {
            shell.Installed.Filter = f;
        }

        shell.CurrentPage = shell.Installed;
    }

    public void ShowBrowse()
    {
        var shell = Shell;
        shell.CurrentPage = shell.Browse;
    }

    public void ShowSettings()
    {
        var shell = Shell;
        shell.CurrentPage = shell.Settings;
    }

    public async Task ShowNexusModAsync(long nexusModId)
    {
        var shell = Shell;
        shell.CurrentPage = shell.Browse;
        _logger.LogDebug("Navigating to Nexus mod {Mod}", nexusModId);
        await shell.Browse.ShowModAsync(nexusModId);
    }
}
