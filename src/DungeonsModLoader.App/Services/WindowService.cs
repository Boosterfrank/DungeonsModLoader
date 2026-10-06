using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using DungeonsModLoader.App.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DungeonsModLoader.App.Services;

/// <summary>
/// Default <see cref="IWindowService"/>: secondary windows come from DI; URLs and folders are handed to the shell.
/// Nothing here throws at the caller: shell failures are logged and swallowed.
/// </summary>
public sealed class WindowService : IWindowService, Nexus.Auth.IUrlOpener
{
    private readonly IServiceProvider _services;
    private readonly ILogger<WindowService> _logger;
    private SwatchWindow? _swatchWindow;

    public WindowService(IServiceProvider services, ILogger<WindowService> logger)
    {
        _services = services;
        _logger = logger;
    }

    public void ShowSwatchWindow()
    {
        var mainWindow = Application.Current?.MainWindow;

        // Started with --swatch: the swatch window already is the main window.
        if (mainWindow is SwatchWindow)
        {
            BringToFront(mainWindow);
            return;
        }

        if (_swatchWindow is not null)
        {
            BringToFront(_swatchWindow);
            return;
        }

        var window = _services.GetRequiredService<SwatchWindow>();
        if (mainWindow is not null && !ReferenceEquals(mainWindow, window) && HasHandle(mainWindow))
        {
            window.Owner = mainWindow;
        }

        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_swatchWindow, window))
            {
                _swatchWindow = null;
            }
        };

        _swatchWindow = window;
        window.Show();
        _logger.LogDebug("Swatch window opened");
    }

    public void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            _logger.LogWarning("Refusing to open non-http(s) URL {Url}", url);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose();
            _logger.LogDebug("Opened URL {Url}", uri.AbsoluteUri);
        }
        catch (Exception ex)
        {
            // No default browser, broken shell association, ... never worth crashing the app over.
            _logger.LogWarning(ex, "Could not open URL {Url}", uri.AbsoluteUri);
        }
    }

    public void OpenFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            _logger.LogWarning("Folder {Path} does not exist; nothing to open", path);
            return;
        }

        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{fullPath}\"") { UseShellExecute = true })?.Dispose();
            _logger.LogDebug("Opened folder {Path}", fullPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not open folder {Path}", fullPath);
        }
    }

    private static bool HasHandle(Window window) => new WindowInteropHelper(window).Handle != IntPtr.Zero;

    private static void BringToFront(Window window)
    {
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
    }
}
