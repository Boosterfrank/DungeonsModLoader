using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DungeonsModLoader.App.Services;
using DungeonsModLoader.App.ViewModels.Setup;
using DungeonsModLoader.Core;
using DungeonsModLoader.Core.Game;
using DungeonsModLoader.Core.Settings;
using Microsoft.Extensions.Logging;

namespace DungeonsModLoader.App.ViewModels.Pages;

/// <summary>Settings page: the game folder card (re-detect / browse) and the About card.</summary>
public sealed partial class SettingsViewModel : PageViewModel
{
    private static readonly TimeSpan ConfirmationDuration = TimeSpan.FromSeconds(5);

    private readonly IWindowService _windows;
    private readonly AppPaths _paths;
    private readonly ISettingsStore _settings;
    private readonly IGameContext _gameContext;
    private readonly IGameLocator _locator;
    private readonly IDialogService _dialogs;
    private readonly IModStoreInitializer _initializer;
    private readonly ILogger<SettingsViewModel> _logger;
    private int _confirmationVersion;

    public SettingsViewModel(
        IWindowService windows,
        AppPaths paths,
        ISettingsStore settings,
        IGameContext gameContext,
        IGameLocator locator,
        IDialogService dialogs,
        IModStoreInitializer initializer,
        ILogger<SettingsViewModel> logger)
    {
        _windows = windows;
        _paths = paths;
        _settings = settings;
        _gameContext = gameContext;
        _locator = locator;
        _dialogs = dialogs;
        _initializer = initializer;
        _logger = logger;

        RefreshGame();
        _gameContext.Changed += OnGameContextChanged;
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

    private void OnGameContextChanged(object? sender, EventArgs e)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            RefreshGame();
        }
        else
        {
            dispatcher.InvokeAsync(RefreshGame);
        }
    }

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
    // About
    // ------------------------------------------------------------------------------------------------------

    [RelayCommand]
    private void OpenSwatches() => _windows.ShowSwatchWindow();

    [RelayCommand]
    private void OpenLogsFolder() => _windows.OpenFolder(_paths.LogsDirectory);

    [RelayCommand]
    private void OpenDataFolder() => _windows.OpenFolder(_paths.Root);
}
