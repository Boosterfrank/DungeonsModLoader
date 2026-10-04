using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using DungeonsModLoader.App.Hosting;
using DungeonsModLoader.App.Services;
using DungeonsModLoader.App.Views;
using DungeonsModLoader.App.Views.Dialogs;
using DungeonsModLoader.App.Views.Setup;
using DungeonsModLoader.Core;
using DungeonsModLoader.Core.Game;
using DungeonsModLoader.Core.Mods;
using DungeonsModLoader.Core.Permissions;
using DungeonsModLoader.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

namespace DungeonsModLoader.App;

/// <summary>
/// Application entry point. Builds the .NET generic host (DI + Serilog), shows the main window and installs
/// the global exception handlers. Everything else gets its dependencies through constructor injection; the
/// registrations live in <see cref="ServiceCollectionExtensions"/>.
/// </summary>
public partial class App : Application
{
    /// <summary>Command-line switch that opens the theme swatch window instead of the main window.</summary>
    private const string SwatchSwitch = "--swatch";

    /// <summary>Command-line switch (followed by a path) that overrides the app data folder.</summary>
    private const string DataDirSwitch = "--data-dir";

    private static readonly TimeSpan HostStopTimeout = TimeSpan.FromSeconds(5);

    private static IHost? _host;

    // Static Serilog access is confined to this file; everything else takes ILogger<T> via DI.
    private Serilog.ILogger _log = Serilog.Core.Logger.None;
    private AppPaths? _paths;
    private bool _mainWindowShown;
    private bool _crashDialogOpen;

    /// <summary>
    /// The application's root service provider, for the rare consumer that DI cannot reach (objects created by
    /// XAML, static helpers). Prefer constructor injection everywhere else. Throws until the host has been built.
    /// </summary>
    public static IServiceProvider Services =>
        _host?.Services ?? throw new InvalidOperationException("The application host has not been built yet.");

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Explicit until the main window is up: the setup wizard and any startup dialog would otherwise become
        // the "main window" and closing them would end the app.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // Hooked before anything else so even a failure in the next few lines ends in the crash dialog.
        RegisterGlobalExceptionHandlers();

        var paths = CreatePaths(e.Args);
        paths.EnsureCreated();
        _paths = paths;

        Log.Logger = CreateLogger(paths);
        _log = Log.ForContext<App>();

        // Milestone 5: nxm:// links arrive as arguments and carry a per-user download token ("key=");
        // mask that query value here before the arguments are written to the log.
        _log.Information(
            "{App} {Version} starting | {OS} ({Arch:l}) | {Runtime} | pid {Pid} | data folder {DataFolder} | args {Args}",
            AppInfo.DisplayName,
            AppInfo.Version,
            RuntimeInformation.OSDescription,
            RuntimeInformation.OSArchitecture.ToString(),
            RuntimeInformation.FrameworkDescription,
            Environment.ProcessId,
            paths.Root,
            e.Args);

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = e.Args,
            ApplicationName = AppInfo.DisplayName,
            ContentRootPath = AppContext.BaseDirectory,
        });

        builder.Logging.ClearProviders();
        builder.Services.AddSerilog(Log.Logger);
        builder.Services
            .UseWpfLifetime()
            .AddAppServices(paths)
            .AddViewModels()
            .AddViews();

        _host = builder.Build();
        await _host.StartAsync();

        var openSwatch = e.Args.Any(a => string.Equals(a, SwatchSwitch, StringComparison.OrdinalIgnoreCase));
        if (openSwatch)
        {
            // Design review tool: no settings, no game folder, no wizard.
            ShowMainWindow(_host.Services.GetRequiredService<SwatchWindow>());
            return;
        }

        if (!await PrepareGameAsync(_host.Services))
        {
            _log.Information("First-run setup was cancelled; closing the app");
            Shutdown(0);
            return;
        }

        ShowMainWindow(_host.Services.GetRequiredService<MainWindow>());
    }

    /// <summary>
    /// Loads the settings and makes sure a game installation is configured: runs the first-run wizard when there is
    /// none (or setup never completed), otherwise points the game context at the stored installation and prepares
    /// the mod store. Returns false only when the user left the wizard without finishing it.
    /// </summary>
    private async Task<bool> PrepareGameAsync(IServiceProvider services)
    {
        var settings = services.GetRequiredService<ISettingsStore>();
        await settings.LoadAsync();

        var installation = settings.Current.ToGameInstallation();
        if (!settings.Current.FirstRunCompleted || installation is null)
        {
            _log.Information(
                "Running first-run setup (first run completed: {FirstRunCompleted}, stored game root usable: {HasInstallation})",
                settings.Current.FirstRunCompleted,
                installation is not null);

            var setup = services.GetRequiredService<SetupWindow>();
            return setup.ShowDialog() == true;
        }

        _log.Information("Using the stored {Source} installation at {Root}", installation.Source, installation.Root);
        services.GetRequiredService<IGameContext>().Set(installation);

        // Access denied offers the one-time permission fix; other failures show a friendly dialog. Either way
        // the app continues: the Installed page shows the problem with a retry button.
        var ready = await services.GetRequiredService<IModStoreInitializer>().InitializeAsync();
        _log.Information(ready ? "Mod store ready" : "Continuing without a ready mod store");
        return true;
    }

    private void ShowMainWindow(Window window)
    {
        MainWindow = window;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        window.Show();
        _mainWindowShown = true;
        _log.Debug("Main window shown: {Window}", window.GetType().Name);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        StopHost();
        _log.Information("{App} shut down (exit code {ExitCode})", AppInfo.DisplayName, e.ApplicationExitCode);
        Log.CloseAndFlush();
        base.OnExit(e);
    }

    /// <summary>
    /// Data folder: <c>%LOCALAPPDATA%\DungeonsModLoader</c>, or the folder given with <c>--data-dir &lt;path&gt;</c>
    /// (developer/testing switch that isolates settings, manifest, logs and cache).
    /// </summary>
    private static AppPaths CreatePaths(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], DataDirSwitch, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(args[i + 1]))
            {
                return new AppPaths(Path.GetFullPath(args[i + 1]));
            }
        }

        return new AppPaths();
    }

    private static Serilog.ILogger CreateLogger(AppPaths paths)
    {
        const long tenMegabytes = 10L * 1024 * 1024;
        const string template = "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

        // Secrets policy: nothing that reaches a log call may contain an API key or download token.
        // Milestone 5 adds a masking helper for Nexus keys next to the Nexus client; use it before logging.
        return new LoggerConfiguration()
            .MinimumLevel.Debug()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System.Net.Http", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.File(
                Path.Combine(paths.LogsDirectory, "app-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                fileSizeLimitBytes: tenMegabytes,
                rollOnFileSizeLimit: true,
                shared: true,
                outputTemplate: template)
            .WriteTo.Debug(outputTemplate: template)
            .CreateLogger();
    }

    private void StopHost()
    {
        var host = Interlocked.Exchange(ref _host, null);
        if (host is null)
        {
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(HostStopTimeout);
            // Stop on the thread pool: hosted-service continuations must never need the UI dispatcher,
            // which is blocked here while the application shuts down.
            Task.Run(() => host.StopAsync(timeout.Token), CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "The host did not stop cleanly within {Timeout}", HostStopTimeout);
        }
        finally
        {
            host.Dispose();
        }
    }

    // ----------------------------------------------------------------------------------------------------------
    // Global error handling. The stock WPF MessageBox is never shown; the themed CrashDialog is used instead.
    // ----------------------------------------------------------------------------------------------------------

    private void RegisterGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _log.Error(e.Exception, "Unhandled exception on the UI thread");

        if (_crashDialogOpen)
        {
            // A second failure while the crash dialog is up: do not recurse into another dialog.
            _log.Fatal(e.Exception, "Exception while the crash dialog was open; closing the app");
            e.Handled = true;
            Shutdown(1);
            return;
        }

        _crashDialogOpen = true;
        try
        {
            // Continuing only makes sense once the main window is up; a startup failure can only close.
            var canContinue = _mainWindowShown;
            var keepRunning = CrashDialog.ShowFor(e.Exception, GetDialogOwner(), _paths ?? new AppPaths(), canContinue);
            e.Handled = true;

            if (keepRunning)
            {
                _log.Information("User chose to continue after the error");
            }
            else
            {
                _log.Information("User chose to close the app after the error");
                Shutdown(1);
            }
        }
        catch (Exception dialogError)
        {
            _log.Fatal(dialogError, "The crash dialog could not be shown; closing the app");
            e.Handled = true;
            Shutdown(1);
        }
        finally
        {
            _crashDialogOpen = false;
        }
    }

    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var exception = e.ExceptionObject as Exception;
        _log.Fatal(exception, "Unhandled exception outside the UI thread (terminating: {IsTerminating}); {ExceptionObject}",
            e.IsTerminating, exception is null ? e.ExceptionObject : null);

        if (e.IsTerminating)
        {
            Log.CloseAndFlush();
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _log.Warning(e.Exception.Flatten(), "Unobserved task exception");
        e.SetObserved();
    }

    private Window? GetDialogOwner()
    {
        foreach (Window window in Windows)
        {
            if (window.IsActive && window.IsVisible && window is not CrashDialog)
            {
                return window;
            }
        }

        return MainWindow is { IsVisible: true } main ? main : null;
    }
}
