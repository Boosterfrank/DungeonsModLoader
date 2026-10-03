using System.IO;
using System.Windows;
using DungeonsModLoader.App.Services;
using DungeonsModLoader.App.ViewModels;
using DungeonsModLoader.App.ViewModels.Pages;
using DungeonsModLoader.App.Views;
using DungeonsModLoader.Core;

namespace DungeonsModLoader.App;

/// <summary>
/// TEMPORARY bootstrap so the shell can be previewed. Replaced by the generic-host startup
/// (DI, Serilog, crash handling) in the milestone 1 hosting work.
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var paths = new AppPaths();
        var windows = new BootstrapWindowService();

        Window window = e.Args.Contains("--swatch", StringComparer.OrdinalIgnoreCase)
            ? new SwatchWindow()
            : new MainWindow(new MainViewModel(
                new InstalledViewModel(),
                new BrowseViewModel(),
                new ProfilesViewModel(),
                new SettingsViewModel(windows, paths)));

        MainWindow = window;
        window.Show();
    }

    private sealed class BootstrapWindowService : IWindowService
    {
        public void ShowSwatchWindow() => new SwatchWindow { Owner = Current.MainWindow }.Show();

        public void OpenUrl(string url)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
            }
        }

        public void OpenFolder(string path)
        {
            if (Directory.Exists(path))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            }
        }
    }
}
