using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DungeonsModLoader.App.Services;
using DungeonsModLoader.App.ViewModels.Dialogs;
using DungeonsModLoader.App.ViewModels.Setup;
using DungeonsModLoader.Core;
using DungeonsModLoader.Core.Game;
using DungeonsModLoader.Core.Settings;
using DungeonsModLoader.Nexus;
using DungeonsModLoader.Nexus.Api;
using DungeonsModLoader.Nexus.Auth;
using Microsoft.Extensions.Logging;

namespace DungeonsModLoader.App.ViewModels.Pages;

/// <summary>Settings page: game folder, Nexus Mods account, nxm:// handler, About.</summary>
public sealed partial class SettingsViewModel : PageViewModel
{
    /// <summary>Spec text shown next to the key field (the connect dialog owns the field; kept for the wizard).</summary>
    public const string NexusKeyInfo = NexusConnectViewModel.KeyInfo;

    private static readonly TimeSpan ConfirmationDuration = TimeSpan.FromSeconds(5);

    private readonly IWindowService _windows;
    private readonly AppPaths _paths;
    private readonly ISettingsStore _settings;
    private readonly IGameContext _gameContext;
    private readonly IGameLocator _locator;
    private readonly IDialogService _dialogs;
    private readonly IModStoreInitializer _initializer;
    private readonly INexusSession _nexus;
    private readonly INexusConnectPrompt _connect;
    private readonly INxmProtocolRegistration _nxm;
    private readonly ILogger<SettingsViewModel> _logger;
    private int _confirmationVersion;
    private int _nexusConfirmationVersion;
    private bool _syncingNxm;

    public SettingsViewModel(
        IWindowService windows,
        AppPaths paths,
        ISettingsStore settings,
        IGameContext gameContext,
        IGameLocator locator,
        IDialogService dialogs,
        IModStoreInitializer initializer,
        INexusSession nexus,
        INexusConnectPrompt connect,
        INxmProtocolRegistration nxm,
        ILogger<SettingsViewModel> logger)
    {
        _windows = windows;
        _paths = paths;
        _settings = settings;
        _gameContext = gameContext;
        _locator = locator;
        _dialogs = dialogs;
        _initializer = initializer;
        _nexus = nexus;
        _connect = connect;
        _nxm = nxm;
        _logger = logger;

        RefreshGame();
        RefreshNexus();
        RefreshNxmHandler();
        _gameContext.Changed += OnGameContextChanged;
        _nexus.Changed += (_, _) => OnUiThread(RefreshNexus);
    }

    public override string Title => "Settings";

    public string AppVersion => AppInfo.Version;

    // ------------------------------------------------------------------------------------------------------
    // Game folder
    // ------------------------------------------------------------------------------------------------------

    /// <summary>Root of the managed installation, or null when none is configured.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGame), nameof(GameRootDisplay))]
    [NotifyCanExecuteChangedFor(nameof(OpenGameFolderCommand))]
    private string? _gameRootPath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSteam), nameof(IsXbox), nameof(IsManual), nameof(GameSourceLabel), nameof(GameLaunchHint))]
    private GameSource _gameSource = GameSource.Manual;

    /// <summary>True while detection, identification or saving runs; the game buttons are locked meanwhile.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RedetectCommand), nameof(BrowseCommand), nameof(OpenGameFolderCommand))]
    private bool _isGameBusy;

    /// <summary>Short inline confirmation ("Game folder updated."); null hides it. Clears itself after a few seconds.</summary>
    [ObservableProperty]
    private string? _gameConfirmation;

    public bool HasGame => !string.IsNullOrEmpty(GameRootPath);

    public string GameRootDisplay => GameRootPath ?? "No game folder selected";

    public bool IsSteam => GameSource == GameSource.Steam;

    public bool IsXbox => GameSource == GameSource.Xbox;

    public bool IsManual => GameSource == GameSource.Manual;

    public string GameSourceLabel => GameSourceLabels.For(GameSource);

    public string GameLaunchHint => GameSourceLabels.LaunchHintFor(GameSource);

    private bool CanOpenGameFolder() => HasGame && !IsGameBusy;

    [RelayCommand(CanExecute = nameof(CanOpenGameFolder))]
    private void OpenGameFolder()
    {
        if (GameRootPath is { } root)
        {
            _windows.OpenFolder(root);
        }
    }

    private bool CanChangeGame() => !IsGameBusy;

    /// <summary>Runs detection again: applies the only (or first) installation found; reports when there is none.</summary>
    [RelayCommand(CanExecute = nameof(CanChangeGame))]
    private async Task RedetectAsync()
    {
        IsGameBusy = true;
        try
        {
            IReadOnlyList<GameInstallation> found;
            try
            {
                found = await _locator.LocateAsync();
            }
            catch (Exception ex)
            {
                // The locator promises not to throw; treat a surprise as "nothing found".
                _logger.LogError(ex, "Re-detecting the game failed");
                found = Array.Empty<GameInstallation>();
            }

            _logger.LogInformation("Re-detect found {Count} installation(s)", found.Count);
            if (found.Count == 0)
            {
                await _dialogs.ShowInfoAsync("Not found", "No installation was detected. Use Browse to pick the folder.");
                return;
            }

            // Keep the install the user already chose when it is still there (e.g. the Xbox copy on a machine that
            // also has Steam); only fall back to the first result when the current one is gone.
            var current = _gameContext.Current;
            var chosen = found.FirstOrDefault(i => current is not null && GamePaths.AreSameFolder(i.Root, current.Root)) ?? found[0];

            var applied = await ApplyAsync(chosen);
            if (applied && found.Count > 1)
            {
                var roots = string.Join(Environment.NewLine, found.Select(i => $"• {GameSourceLabels.For(i.Source)}: {i.Root}"));
                await _dialogs.ShowInfoAsync(
                    "Several installations found",
                    $"{GameSourceLabels.For(chosen.Source)} at {chosen.Root} is in use. Use Browse to switch to another:{Environment.NewLine}{Environment.NewLine}{roots}");
            }
        }
        finally
        {
            IsGameBusy = false;
        }
    }

    /// <summary>Lets the user pick the game folder by hand; a folder without <c>Dungeons\Content\Paks</c> is rejected.</summary>
    [RelayCommand(CanExecute = nameof(CanChangeGame))]
    private async Task BrowseAsync()
    {
        var folder = _dialogs.PickFolder($"Select the {AppInfo.GameDisplayName} folder", GameRootPath);
        if (folder is null)
        {
            return;
        }

        IsGameBusy = true;
        try
        {
            GameInstallation? identified = null;
            try
            {
                identified = await _locator.IdentifyAsync(folder);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Identifying the picked folder {Folder} failed", folder);
            }

            if (identified is null)
            {
                _logger.LogInformation("Picked folder {Folder} is not a game root", folder);
                await _dialogs.ShowErrorAsync(
                    "Not a game folder",
                    $@"That folder doesn't contain Dungeons\Content\Paks. Pick the folder that contains the Dungeons folder (for Steam: steamapps\common\{AppInfo.GameDisplayName}).");
                return;
            }

            await ApplyAsync(identified);
        }
        finally
        {
            IsGameBusy = false;
        }
    }

    /// <summary>
    /// Stores, saves and activates <paramref name="installation"/>, then prepares the mod store for it (offering the
    /// permission fix when the folders are not writable). A failed save restores the previous stored values.
    /// </summary>
    private async Task<bool> ApplyAsync(GameInstallation installation)
    {
        var settings = _settings.Current;
        var (previousRoot, previousSource, previousSteamAppId, previousXboxId) =
            (settings.GameRootPath, settings.GameSource, settings.SteamAppId, settings.XboxAppUserModelId);

        settings.ApplyGameInstallation(installation);
        try
        {
            await _settings.SaveAsync();
        }
        catch (Exception ex)
        {
            // Restore the raw stored fields (not a re-derived installation, which would be null if the old root is gone).
            settings.GameRootPath = previousRoot;
            settings.GameSource = previousSource;
            settings.SteamAppId = previousSteamAppId;
            settings.XboxAppUserModelId = previousXboxId;
            _logger.LogError(ex, "Saving the game folder failed");
            await _dialogs.ShowErrorAsync(
                "Settings could not be saved",
                $"The new game folder could not be written to {_paths.SettingsFile}. Make sure the folder is writable and try again.",
                ex.ToString());
            return false;
        }

        _logger.LogInformation("Game folder set to {Source} install at {Root}", installation.Source, installation.Root);
        _gameContext.Set(installation);
        RefreshGame();

        var ready = await _initializer.InitializeAsync();
        _ = ShowConfirmationAsync(ready ? "Game folder updated." : "Game folder updated, but the mod list is not ready yet.");
        return true;
    }

    private void RefreshGame()
    {
        var current = _gameContext.Current;
        GameRootPath = current?.Root;
        GameSource = current?.Source ?? GameSource.Manual;
    }

    private void OnGameContextChanged(object? sender, EventArgs e) => OnUiThread(RefreshGame);

    private async Task ShowConfirmationAsync(string text)
    {
        var version = ++_confirmationVersion;
        GameConfirmation = text;
        await Task.Delay(ConfirmationDuration);
        if (version == _confirmationVersion)
        {
            GameConfirmation = null;
        }
    }

    // ------------------------------------------------------------------------------------------------------
    // Nexus Mods account
    // ------------------------------------------------------------------------------------------------------

    /// <summary>True in builds where Nexus has issued the app slug: "Log in with Nexus" is offered next to the key dialog.</summary>
    public bool IsSsoAvailable => NexusConstants.IsSsoAvailable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNexusLoggedIn), nameof(IsNexusLoggedOut), nameof(IsNexusUnverified), nameof(IsNexusVerifying), nameof(NexusStatusText), nameof(ShowNexusAccount), nameof(DownloadSetupText), nameof(ShowNxmTurnOnLink))]
    [NotifyCanExecuteChangedFor(nameof(ConnectNexusCommand), nameof(LoginWithSsoCommand), nameof(LogoutNexusCommand), nameof(RevalidateNexusCommand))]
    private NexusSessionStatus _nexusStatus;

    [ObservableProperty]
    private string _nexusUserName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DownloadSetupText), nameof(ShowNxmTurnOnLink))]
    private bool _nexusIsPremium;

    [ObservableProperty]
    private string _nexusMembership = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectNexusCommand), nameof(LoginWithSsoCommand), nameof(LogoutNexusCommand), nameof(RevalidateNexusCommand))]
    private bool _isNexusBusy;

    [ObservableProperty]
    private string? _nexusBusyText;

    /// <summary>Inline error under the account block; null hides it.</summary>
    [ObservableProperty]
    private string? _nexusError;

    [ObservableProperty]
    private string? _nexusConfirmation;

    public bool IsNexusLoggedIn => NexusStatus == NexusSessionStatus.LoggedIn;

    public bool IsNexusLoggedOut => NexusStatus == NexusSessionStatus.LoggedOut;

    public bool IsNexusUnverified => NexusStatus == NexusSessionStatus.Unverified;

    public bool IsNexusVerifying => NexusStatus == NexusSessionStatus.Verifying;

    /// <summary>The account block (name, membership, log out) shows for a stored key, verified or not.</summary>
    public bool ShowNexusAccount => NexusStatus is NexusSessionStatus.LoggedIn or NexusSessionStatus.Unverified;

    public string NexusStatusText => NexusStatus switch
    {
        NexusSessionStatus.LoggedIn => "Connected",
        NexusSessionStatus.Unverified => "Key stored, not verified (Nexus Mods could not be reached)",
        NexusSessionStatus.Verifying => "Checking...",
        _ => "Not connected",
    };

    /// <summary>How downloads work for this account, and whether the link handler still needs turning on.</summary>
    public string DownloadSetupText
    {
        get
        {
            if (!ShowNexusAccount)
            {
                return string.Empty;
            }

            if (NexusIsPremium)
            {
                return "Premium membership: downloads run inside the app.";
            }

            return IsNxmHandler
                ? "Free account: downloads start on the Nexus Mods website (\"Slow download\" / \"Mod Manager Download\") and land here automatically."
                : "Free account: downloads start on the Nexus Mods website. For the files to land here, this app has to handle the site's nxm:// links.";
        }
    }

    /// <summary>Shows the "Turn on now" link under the download text (free account, handler not pointing here).</summary>
    public bool ShowNxmTurnOnLink => ShowNexusAccount && !NexusIsPremium && !IsNxmHandler;

    private bool CanConnectNexus() => !IsNexusBusy;

    /// <summary>"Connect Nexus Mods account": the guided dialog (open key page, copy, paste, verify &amp; save).</summary>
    [RelayCommand(CanExecute = nameof(CanConnectNexus))]
    private async Task ConnectNexusAsync()
    {
        NexusError = null;
        var connected = await _connect.ShowAsync();
        RefreshNexus();
        if (connected)
        {
            _ = ShowNexusConfirmationAsync($"Connected as {_nexus.User?.Name ?? "your account"}.");
        }
    }

    private bool CanLoginWithSso() => !IsNexusBusy && IsSsoAvailable;

    /// <summary>"Log in with Nexus": browser approval over the SSO websocket (only when the app slug is set).</summary>
    [RelayCommand(CanExecute = nameof(CanLoginWithSso))]
    private async Task LoginWithSsoAsync()
    {
        var provider = _nexus.Providers.FirstOrDefault(p => p.Method == NexusAuthMethod.Sso);
        if (provider is null)
        {
            return;
        }

        await RunNexusAsync("Waiting for your approval in the browser...", async () =>
        {
            var user = await _dialogs.RunWithProgressAsync(
                "Log in with Nexus Mods",
                (progress, cancellationToken) => _nexus.LoginAsync(provider, null, new Progress<string>(m => progress.Report(new ProgressUpdate(m))), cancellationToken));
            _ = ShowNexusConfirmationAsync($"Connected as {user.Name}.");
        });
    }

    private bool CanLogoutNexus() => !IsNexusBusy && _nexus.HasApiKey;

    [RelayCommand(CanExecute = nameof(CanLogoutNexus))]
    private async Task LogoutNexusAsync()
    {
        var confirmed = await _dialogs.ConfirmAsync(
            "Log out of Nexus Mods?",
            "The stored API key is removed from this PC. Installed mods stay as they are; downloads and update checks need a key again.",
            "Log out");
        if (!confirmed)
        {
            return;
        }

        await RunNexusAsync("Removing the key...", async () =>
        {
            await _nexus.LogoutAsync();
            _ = ShowNexusConfirmationAsync("Logged out. The key was removed.");
        });
    }

    private bool CanRevalidateNexus() => !IsNexusBusy && _nexus.HasApiKey;

    [RelayCommand(CanExecute = nameof(CanRevalidateNexus))]
    private Task RevalidateNexusAsync() =>
        RunNexusAsync("Checking the key with Nexus Mods...", async () =>
        {
            var user = await _nexus.RevalidateAsync();
            if (user is not null)
            {
                _ = ShowNexusConfirmationAsync($"Verified: connected as {user.Name}.");
            }
            else if (_nexus.LastError is { } error)
            {
                NexusError = error;
            }
        });

    [RelayCommand]
    private void OpenNexusGamePage() => _windows.OpenUrl($"{NexusConstants.WebsiteBaseUrl}/games/{NexusConstants.GameDomain}");

    private async Task RunNexusAsync(string busyText, Func<Task> work)
    {
        if (IsNexusBusy)
        {
            return;
        }

        NexusError = null;
        NexusBusyText = busyText;
        IsNexusBusy = true;
        try
        {
            await work();
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Nexus account action cancelled: {Action}", busyText);
        }
        catch (NexusException ex)
        {
            // Phrased for the user by the Nexus layer ("rejected", "offline").
            _logger.LogWarning("Nexus account action failed: {Message}", ex.Message);
            NexusError = ex.Message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Nexus account action failed unexpectedly: {Action}", busyText);
            NexusError = "Something went wrong. See the log for details.";
        }
        finally
        {
            IsNexusBusy = false;
            NexusBusyText = null;
            RefreshNexus();
        }
    }

    private void RefreshNexus()
    {
        NexusStatus = _nexus.Status;
        NexusUserName = _nexus.User?.Name ?? string.Empty;
        NexusIsPremium = _nexus.IsPremium;
        NexusMembership = _nexus.User is null ? string.Empty : _nexus.IsPremium ? "Premium member" : "Free account";
        if (NexusError is null && _nexus.Status == NexusSessionStatus.Unverified && _nexus.LastError is { } error)
        {
            NexusError = error;
        }

        LogoutNexusCommand.NotifyCanExecuteChanged();
        RevalidateNexusCommand.NotifyCanExecuteChanged();
    }

    private async Task ShowNexusConfirmationAsync(string text)
    {
        var version = ++_nexusConfirmationVersion;
        NexusConfirmation = text;
        await Task.Delay(ConfirmationDuration);
        if (version == _nexusConfirmationVersion)
        {
            NexusConfirmation = null;
        }
    }

    // ------------------------------------------------------------------------------------------------------
    // nxm:// handler
    // ------------------------------------------------------------------------------------------------------

    /// <summary>Bound to the toggle; changing it registers / unregisters the handler right away.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DownloadSetupText), nameof(ShowNxmTurnOnLink))]
    private bool _isNxmHandler;

    [ObservableProperty]
    private string _nxmHandlerHint = string.Empty;

    partial void OnIsNxmHandlerChanged(bool value)
    {
        if (_syncingNxm)
        {
            return;
        }

        try
        {
            if (value)
            {
                _nxm.Register();
            }
            else
            {
                _nxm.Unregister();
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException or InvalidOperationException)
        {
            _logger.LogError(ex, "Changing the nxm:// handler registration failed");
            _ = _dialogs.ShowErrorAsync("Could not change the link handler", "Windows did not allow the change to the nxm:// registration. See the log for details.", ex.ToString());
        }

        RefreshNxmHandler();
    }

    /// <summary>The "Turn on now" link in the account card.</summary>
    [RelayCommand]
    private void EnableNxmHandler() => IsNxmHandler = true;

    private void RefreshNxmHandler()
    {
        var state = _nxm.GetState();
        _syncingNxm = true;
        try
        {
            IsNxmHandler = state.IsThisApp;
        }
        finally
        {
            _syncingNxm = false;
        }

        NxmHandlerHint = state.IsThisApp
            ? "\"Mod Manager Download\" and \"Slow download\" buttons on Nexus Mods send files to this app."
            : state.IsOtherApp
                ? $"{state.HandlerName ?? "Another program"} currently handles nxm:// links. Turning this on lets {AppInfo.DisplayName} receive them instead (for your Windows account only; turn it off to hand them back)."
                : "Lets the \"Mod Manager Download\" and \"Slow download\" buttons on Nexus Mods send files to this app (free accounts download this way).";
    }

    // ------------------------------------------------------------------------------------------------------
    // About
    // ------------------------------------------------------------------------------------------------------

    [RelayCommand]
    private void OpenSwatches() => _windows.ShowSwatchWindow();

    [RelayCommand]
    private void OpenLogsFolder() => _windows.OpenFolder(_paths.LogsDirectory);

    [RelayCommand]
    private void OpenDataFolder() => _windows.OpenFolder(_paths.Root);

    private static void OnUiThread(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.InvokeAsync(action);
        }
    }
}
