using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DungeonsModLoader.App.Services;
using DungeonsModLoader.App.ViewModels.Pages;
using DungeonsModLoader.Core;
using DungeonsModLoader.Core.Game;
using DungeonsModLoader.Core.Mods;
using Microsoft.Extensions.Logging;

namespace DungeonsModLoader.App.ViewModels;

/// <summary>
/// Shell view model: owns the four pages, the current selection, the Play action and the game-running state.
/// It starts the process monitor for the configured installation and restarts it whenever the installation changes.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    /// <summary>How long a transient status message stays in the title bar.</summary>
    private static readonly TimeSpan StatusLifetime = TimeSpan.FromSeconds(5);

    private readonly IGameLauncher _launcher;
    private readonly IGameContext _game;
    private readonly IGameProcessMonitor _monitor;
    private readonly IModService _mods;
    private readonly IDialogService _dialogs;
    private readonly ILogger<MainViewModel> _logger;

    /// <summary>Incremented per status message so a delayed clear only removes the message it was scheduled for.</summary>
    private int _statusVersion;

    public MainViewModel(
        InstalledViewModel installed,
        BrowseViewModel browse,
        ProfilesViewModel profiles,
        SettingsViewModel settings,
        IGameLauncher launcher,
        IGameContext game,
        IGameProcessMonitor monitor,
        IModService mods,
        IDialogService dialogs,
        ILogger<MainViewModel> logger)
    {
        Installed = installed;
        Browse = browse;
        Profiles = profiles;
        Settings = settings;
        _launcher = launcher;
        _game = game;
        _monitor = monitor;
        _mods = mods;
        _dialogs = dialogs;
        _logger = logger;

        _currentPage = installed;
        _isGameRunning = _monitor.IsGameRunning;
        _gameSourceLabel = LabelFor(_game.Current);

        _game.Changed += OnGameChanged;
        _monitor.GameRunningChanged += OnGameRunningChanged;
        ApplyGameContext();
    }

    public InstalledViewModel Installed { get; }
    public BrowseViewModel Browse { get; }
    public ProfilesViewModel Profiles { get; }
    public SettingsViewModel Settings { get; }

    /// <summary>The page shown in the content area. Never null; defaults to <see cref="Installed"/>.</summary>
    [ObservableProperty]
    private PageViewModel _currentPage;

    /// <summary>Transient message shown in the title bar (empty = hidden).</summary>
    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>True while a game process is running; the Play button reads "RUNNING" and mod changes are locked.</summary>
    [ObservableProperty]
    private bool _isGameRunning;

    /// <summary>"Steam" / "Xbox app" / "Manual" for the caption under the Play button.</summary>
    [ObservableProperty]
    private string _gameSourceLabel;

    /// <summary>Switches the content area to <paramref name="page"/> (sidebar items and Ctrl+1..4 use this).</summary>
    [RelayCommand]
    private void Navigate(PageViewModel? page)
    {
        if (page is not null)
        {
            CurrentPage = page;
        }
    }

    /// <summary>
    /// Launches the game: makes sure the mod folders are in place (milestone 4 applies the active profile here),
    /// then starts it through Steam, the Xbox app or the executable. Launch problems become a friendly dialog.
    /// </summary>
    [RelayCommand]
    private async Task PlayAsync()
    {
        if (IsGameRunning)
        {
            await _dialogs.ShowInfoAsync("Already running", $"{AppInfo.GameDisplayName} is already running. Close it before launching it again.");
            return;
        }

        var installation = _game.Current;
        if (installation is null)
        {
            await _dialogs.ShowErrorAsync("Game folder not set", $"Choose your {AppInfo.GameDisplayName} folder in Settings before launching.");
            return;
        }

        SetStatus($"Launching {AppInfo.GameDisplayName}...", autoClear: false);
        try
        {
            await _mods.ReconcileAsync();
            await _launcher.LaunchAsync(installation);
            _logger.LogInformation("Game launched via {Source} from {Root}", installation.Source, installation.Root);
            SetStatus($"{AppInfo.GameDisplayName} is starting...", autoClear: true);
        }
        catch (GameLaunchException ex)
        {
            _logger.LogError(ex, "Game launch failed");
            SetStatus(string.Empty, autoClear: false);
            await _dialogs.ShowErrorAsync("Could not launch the game", ex.Message, ex.InnerException?.ToString());
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ModAccessDeniedException)
        {
            _logger.LogError(ex, "Mod folders could not be prepared before launch");
            SetStatus(string.Empty, autoClear: false);
            await _dialogs.ShowErrorAsync(
                "Could not prepare the mod folders",
                "The mod folders could not be checked before launch. If the game is already running, close it and try again.",
                ex.ToString());
            return;
        }

        try
        {
            // Pick the new process up right away instead of waiting for the next poll.
            await _monitor.RefreshAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Process monitor refresh after launch failed");
        }
    }

    /// <summary>Caption text for an installation's source.</summary>
    public static string LabelFor(GameInstallation? installation) => installation?.Source switch
    {
        GameSource.Steam => "Steam",
        GameSource.Xbox => "Xbox app",
        GameSource.Manual => "Manual",
        _ => "Not set up",
    };

    private void OnGameChanged(object? sender, EventArgs e) => OnUiThread(ApplyGameContext);

    private void OnGameRunningChanged(object? sender, bool running) => OnUiThread(() => IsGameRunning = running);

    private void ApplyGameContext()
    {
        var current = _game.Current;
        GameSourceLabel = LabelFor(current);

        if (current is null)
        {
            _monitor.Stop();
            IsGameRunning = false;
            _logger.LogDebug("No game installation configured; process monitor stopped");
        }
        else
        {
            _monitor.Start(current);
            _logger.LogDebug("Process monitor started for {Root} ({Source})", current.Root, current.Source);
        }
    }

    private void SetStatus(string text, bool autoClear)
    {
        var version = ++_statusVersion;
        StatusText = text;

        if (autoClear && text.Length > 0)
        {
            _ = ClearStatusLaterAsync(version);
        }
    }

    private async Task ClearStatusLaterAsync(int version)
    {
        await Task.Delay(StatusLifetime);
        if (version == _statusVersion)
        {
            StatusText = string.Empty;
        }
    }

    /// <summary>Runs on the UI thread (the context and the monitor raise their events on arbitrary threads).</summary>
    private static void OnUiThread(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action);
        }
    }
}
