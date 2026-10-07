using System.Diagnostics;
using System.IO;
using System.Windows;
using DungeonsModLoader.Core;
using DungeonsModLoader.Core.AppUpdates;
using DungeonsModLoader.Core.Settings;
using DungeonsModLoader.Nexus.Download;
using Microsoft.Extensions.Logging;

namespace DungeonsModLoader.App.Services;

/// <summary>
/// Self-update through GitHub Releases: checks <c>releases/latest</c> of the app's repository at every start and
/// on demand from Settings, exposes the newer version for the banner, downloads its installer with a progress
/// dialog and starts it silently, after which the app closes. After two starts on an outdated version the update
/// becomes mandatory (one-button dialog, then the install).
/// </summary>
public interface IAppUpdateService
{
    /// <summary>The newer release found by the last check; null when up to date or not checked.</summary>
    AppUpdateInfo? Available { get; }

    bool IsChecking { get; }

    /// <summary>The user closed the banner for this session.</summary>
    bool IsDismissed { get; }

    /// <summary>User-facing reason the last check failed; null when it succeeded.</summary>
    string? LastError { get; }

    DateTimeOffset? LastCheckedUtc { get; }

    /// <summary>Raised on the UI thread whenever any of the properties change.</summary>
    event EventHandler? Changed;

    /// <summary>
    /// Asks GitHub for the latest release. Without <paramref name="force"/> the call is skipped when the setting is
    /// off. Never throws; failures land in <see cref="LastError"/>.
    /// </summary>
    Task<AppUpdateInfo?> CheckAsync(bool force = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// The start-up check: honours the setting, counts starts on an outdated version and, past the allowance,
    /// shows the mandatory dialog and installs the update. Never throws.
    /// </summary>
    Task CheckAtStartupAsync();

    /// <summary>Downloads the installer (progress dialog), starts it and shuts the app down. Problems become dialogs.</summary>
    Task InstallAsync(AppUpdateInfo update, bool mandatory = false);

    void Dismiss();
}

/// <inheritdoc cref="IAppUpdateService"/>
public sealed class AppUpdateService : IAppUpdateService
{
    /// <summary>Inno Setup switches: no wizard pages, close the (already exiting) app if needed, never reboot.</summary>
    private const string InstallerArguments = "/SILENT /CLOSEAPPLICATIONS /NORESTART";

    private readonly IGitHubReleaseClient _client;
    private readonly ISettingsStore _settings;
    private readonly IDownloadService _downloads;
    private readonly IDialogService _dialogs;
    private readonly IWindowService _windows;
    private readonly AppPaths _paths;
    private readonly ILogger<AppUpdateService> _logger;

    public AppUpdateService(
        IGitHubReleaseClient client,
        ISettingsStore settings,
        IDownloadService downloads,
        IDialogService dialogs,
        IWindowService windows,
        AppPaths paths,
        ILogger<AppUpdateService> logger)
    {
        _client = client;
        _settings = settings;
        _downloads = downloads;
        _dialogs = dialogs;
        _windows = windows;
        _paths = paths;
        _logger = logger;
    }

    public AppUpdateInfo? Available { get; private set; }

    public bool IsChecking { get; private set; }

    public bool IsDismissed { get; private set; }

    public string? LastError { get; private set; }

    public DateTimeOffset? LastCheckedUtc { get; private set; }

    public event EventHandler? Changed;

    public async Task<AppUpdateInfo?> CheckAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        if (IsChecking)
        {
            return Available;
        }

        var settings = _settings.Current;
        if (!force && !settings.CheckForAppUpdates)
        {
            _logger.LogDebug("App update check skipped: turned off in Settings");
            return null;
        }

        IsChecking = true;
        LastError = null;
        RaiseChanged();
        try
        {
            var release = await _client.GetLatestReleaseAsync(AppInfo.GitHubRepository, cancellationToken);
            var update = AppUpdateResolver.Resolve(release, AppInfo.Version);
            if (update is not null && Available?.VersionText != update.VersionText)
            {
                IsDismissed = false; // a newer release than the one dismissed shows the banner again
            }

            Available = update;
            LastCheckedUtc = DateTimeOffset.UtcNow;
            _logger.LogInformation(
                "App update check: latest release {Tag}; running {Current}; update available: {Available}",
                release?.TagName ?? "(none)", AppInfo.Version, update?.VersionText ?? "no");

            settings.LastAppUpdateCheckUtc = LastCheckedUtc;
            await SaveSettingsQuietlyAsync(cancellationToken);
            return update;
        }
        catch (AppUpdateException ex)
        {
            _logger.LogWarning("App update check failed: {Message}", ex.Message);
            LastError = ex.Message;
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "App update check failed unexpectedly");
            LastError = "Something went wrong while checking for updates. See the log for details.";
            return null;
        }
        finally
        {
            IsChecking = false;
            RaiseChanged();
        }
    }

    public async Task CheckAtStartupAsync()
    {
        var settings = _settings.Current;
        if (!settings.CheckForAppUpdates)
        {
            _logger.LogDebug("Startup app update check skipped: turned off in Settings");
            return;
        }

        var update = await CheckAsync(force: true);
        if (update is null)
        {
            // Up to date (or the check failed; a failure must not wipe the count). Nothing more to do.
            if (LastError is null && settings.OutdatedLaunchCount != 0)
            {
                settings.OutdatedLaunchCount = 0;
                await SaveSettingsQuietlyAsync();
            }

            return;
        }

        settings.OutdatedLaunchCount++;
        await SaveSettingsQuietlyAsync();
        _logger.LogInformation(
            "Started {Current} while {Newer} is available: outdated start {Count} (mandatory after {Free})",
            AppInfo.Version, update.VersionText, settings.OutdatedLaunchCount, AppUpdatePolicy.FreeOutdatedLaunches);

        if (!AppUpdatePolicy.IsMandatory(settings.OutdatedLaunchCount))
        {
            return; // the banner is enough this time
        }

        await _dialogs.ShowRequiredAsync(
            "Update required",
            $"This version of {AppInfo.DisplayName} ({AppInfo.Version}) is out of date. Version {update.VersionText} will now be downloaded and installed; "
            + "the app restarts when it is done. Your mods, settings and profiles are kept.",
            "OK");
        await InstallAsync(update, mandatory: true);
    }

    public async Task InstallAsync(AppUpdateInfo update, bool mandatory = false)
    {
        ArgumentNullException.ThrowIfNull(update);
        _logger.LogInformation("Downloading app update {Version} from {Url}{Mandatory}", update.VersionText, update.Installer.DownloadUrl, mandatory ? " (mandatory)" : string.Empty);

        string installerPath;
        try
        {
            installerPath = await _dialogs.RunWithProgressAsync(
                $"Downloading {AppInfo.DisplayName} {update.VersionText}",
                (progress, cancellationToken) =>
                {
                    var adapter = new Progress<DownloadProgress>(d => progress.Report(new ProgressUpdate(ViewModels.Format.Download(d.BytesReceived, d.TotalBytes, d.BytesPerSecond), d.Fraction)));
                    return _downloads.DownloadAsync(update.Installer.DownloadUrl, _paths.DownloadsDirectory, update.Installer.Name, adapter, cancellationToken);
                },
                canCancel: !mandatory);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("App update download cancelled");
            return;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "App update download failed");
            await _dialogs.ShowErrorAsync(
                "Could not download the update",
                "The installer could not be downloaded. Check your internet connection and try again, or download it from the releases page.",
                ex.ToString());
            return;
        }

        if (update.Installer.Size > 0 && new FileInfo(installerPath).Length != update.Installer.Size)
        {
            _logger.LogWarning("Downloaded installer has {Actual} bytes, expected {Expected}", new FileInfo(installerPath).Length, update.Installer.Size);
            TryDelete(installerPath);
            await _dialogs.ShowErrorAsync("Could not download the update", "The downloaded installer is incomplete. Try again, or download it from the releases page.");
            return;
        }

        try
        {
            _logger.LogInformation("Starting installer {Path} {Args}; closing the app for the update", installerPath, InstallerArguments);
            Process.Start(new ProcessStartInfo(installerPath) { Arguments = InstallerArguments, UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // SmartScreen / UAC declined, or the file is blocked: hand the user the file instead of failing silently.
            _logger.LogWarning(ex, "The installer could not be started");
            var open = await _dialogs.ConfirmAsync(
                "Could not start the installer",
                $"Windows did not let the app start the installer. It was saved as{Environment.NewLine}{installerPath}{Environment.NewLine}{Environment.NewLine}You can run it yourself.",
                "Open folder",
                "Close");
            if (open)
            {
                _windows.OpenFolder(_paths.DownloadsDirectory);
            }

            return;
        }

        Application.Current?.Shutdown();
    }

    public void Dismiss()
    {
        if (IsDismissed)
        {
            return;
        }

        IsDismissed = true;
        _logger.LogDebug("App update banner dismissed for this session");
        RaiseChanged();
    }

    private async Task SaveSettingsQuietlyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _settings.SaveAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "The app update settings could not be saved");
        }
    }

    private void RaiseChanged()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            dispatcher.BeginInvoke(() => Changed?.Invoke(this, EventArgs.Empty));
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Incomplete installer {Path} could not be deleted", path);
        }
    }
}
