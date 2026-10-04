using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DungeonsModLoader.App.Services;
using DungeonsModLoader.Core;
using DungeonsModLoader.Core.Game;
using DungeonsModLoader.Core.Mods;
using DungeonsModLoader.Core.Permissions;
using DungeonsModLoader.Core.Settings;
using DungeonsModLoader.Nexus;
using Microsoft.Extensions.Logging;

namespace DungeonsModLoader.App.ViewModels.Pages;

/// <summary>Filter chips above the mod list.</summary>
public enum ModFilter
{
    All = 0,
    Enabled = 1,
    Disabled = 2,

    /// <summary>Empty until the Nexus update check arrives (milestone 5); the chip exists so the layout is final.</summary>
    UpdatesAvailable = 3,
}

/// <summary>
/// Installed page: the mod list with enable/disable, import of unmanaged folders, rename and uninstall.
/// Rows mirror <see cref="IModService.Mods"/> + <see cref="IModService.UnmanagedFolders"/> and are updated in place
/// (matched by id / folder name) whenever the service reports a change, so toggles never jump. Every mod-changing
/// action is locked while the game is running or a long operation is in progress.
/// </summary>
public sealed partial class InstalledViewModel : PageViewModel
{
    private const string AccessDeniedMessage =
        "Windows did not allow changes to the mod folder. Fix the folder permissions and try again.";

    private readonly IModService _mods;
    private readonly IGameContext _game;
    private readonly IGameProcessMonitor _monitor;
    private readonly ISettingsStore _settings;
    private readonly IDialogService _dialogs;
    private readonly IModStoreInitializer _initializer;
    private readonly IWindowService _windows;
    private readonly ILogger<InstalledViewModel> _logger;

    /// <summary>Every known row by <see cref="ModRowViewModel.Key"/>, so refreshes reuse instances.</summary>
    private readonly Dictionary<string, ModRowViewModel> _rowsByKey = new(StringComparer.Ordinal);

    public InstalledViewModel(
        IModService mods,
        IGameContext game,
        IGameProcessMonitor monitor,
        ISettingsStore settings,
        IDialogService dialogs,
        IModStoreInitializer initializer,
        IWindowService windows,
        ILogger<InstalledViewModel> logger)
    {
        _mods = mods;
        _game = game;
        _monitor = monitor;
        _settings = settings;
        _dialogs = dialogs;
        _initializer = initializer;
        _windows = windows;
        _logger = logger;

        _isGameRunning = _monitor.IsGameRunning;
        _activeProfile = _settings.Current.ActiveProfile;

        _mods.Changed += OnModsChanged;
        _monitor.GameRunningChanged += OnGameRunningChanged;
        _settings.Saved += OnSettingsSaved;

        // The service may already be initialized (or not; then the Changed event fills the list later).
        RebuildRows();
    }

    public override string Title => "Installed";

    /// <summary>All rows: managed mods first (as sorted by the service), then unmanaged folders.</summary>
    public ObservableCollection<ModRowViewModel> Rows { get; } = new();

    /// <summary>The rows that pass <see cref="SearchText"/> and <see cref="Filter"/>, in <see cref="Rows"/> order.</summary>
    public ObservableCollection<ModRowViewModel> VisibleRows { get; } = new();

    // ----------------------------------------------------------------------------------------------------------
    // Search & filter
    // ----------------------------------------------------------------------------------------------------------

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAllFilter))]
    [NotifyPropertyChangedFor(nameof(IsEnabledFilter))]
    [NotifyPropertyChangedFor(nameof(IsDisabledFilter))]
    [NotifyPropertyChangedFor(nameof(IsUpdatesFilter))]
    private ModFilter _filter = ModFilter.All;

    // One boolean per chip so the RadioButtons bind without a converter; setting false (another chip was picked) is ignored.
    public bool IsAllFilter
    {
        get => Filter == ModFilter.All;
        set => SetFilterIf(value, ModFilter.All);
    }

    public bool IsEnabledFilter
    {
        get => Filter == ModFilter.Enabled;
        set => SetFilterIf(value, ModFilter.Enabled);
    }

    public bool IsDisabledFilter
    {
        get => Filter == ModFilter.Disabled;
        set => SetFilterIf(value, ModFilter.Disabled);
    }

    public bool IsUpdatesFilter
    {
        get => Filter == ModFilter.UpdatesAvailable;
        set => SetFilterIf(value, ModFilter.UpdatesAvailable);
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnFilterChanged(ModFilter value) => ApplyFilter();

    private void SetFilterIf(bool selected, ModFilter filter)
    {
        if (selected)
        {
            Filter = filter;
        }
    }

    // ----------------------------------------------------------------------------------------------------------
    // Counts, footer, state
    // ----------------------------------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FooterText))]
    private int _totalCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FooterText))]
    private int _enabledCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FooterText))]
    private string _activeProfile;

    /// <summary>"12 mods · 9 enabled · Profile: Default".</summary>
    public string FooterText =>
        $"{TotalCount} {(TotalCount == 1 ? "mod" : "mods")} · {EnabledCount} enabled · Profile: {ActiveProfile}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    private bool _hasAnyRows;

    /// <summary>The mod store could not be prepared (manifest unreadable, folders not accessible): show an error with a retry.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    private bool _showInitError;

    [ObservableProperty]
    private string _initErrorText = string.Empty;

    /// <summary>No mods at all (neither managed nor unmanaged) and no error: show the "No mods installed yet" call to action.</summary>
    public bool ShowEmptyState => !HasAnyRows && !ShowInitError;

    /// <summary>Tries to prepare the mod store again (offers the permission fix when access is denied).</summary>
    [RelayCommand]
    private Task RetryInitializeAsync() =>
        RunBusyAsync("Preparing the mod list...", async () =>
        {
            var ready = await _initializer.InitializeAsync();
            _logger.LogInformation("Mod store retry from the Installed page: ready = {Ready}", ready);
        });

    /// <summary>
    /// Re-checks the lock after a dialog returned: the game may have started while the dialog was open.
    /// Returns false (after telling the user) when mod changes are not allowed right now.
    /// </summary>
    private async Task<bool> EnsureCanMutateAsync()
    {
        if (CanMutate)
        {
            return true;
        }

        if (IsGameRunning)
        {
            await _dialogs.ShowInfoAsync("Game running", $"{AppInfo.GameDisplayName} is running. Close it before changing mods.");
        }

        return false;
    }

    /// <summary>There are mods, but none passes the current search / filter.</summary>
    [ObservableProperty]
    private bool _showNoMatches;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanMutate))]
    [NotifyCanExecuteChangedFor(nameof(EnableAllCommand))]
    [NotifyCanExecuteChangedFor(nameof(DisableAllCommand))]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand))]
    [NotifyCanExecuteChangedFor(nameof(RenameCommand))]
    [NotifyCanExecuteChangedFor(nameof(UninstallCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveMissingCommand))]
    private bool _isGameRunning;

    /// <summary>A long operation (bulk enable/disable, import, uninstall) is running; the list is locked meanwhile.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanMutate))]
    [NotifyCanExecuteChangedFor(nameof(EnableAllCommand))]
    [NotifyCanExecuteChangedFor(nameof(DisableAllCommand))]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand))]
    [NotifyCanExecuteChangedFor(nameof(RenameCommand))]
    [NotifyCanExecuteChangedFor(nameof(UninstallCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveMissingCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _busyText = string.Empty;

    /// <summary>Mod changes are allowed: the game is not running and no long operation is in progress.</summary>
    public bool CanMutate => !IsGameRunning && !IsBusy;

    partial void OnIsGameRunningChanged(bool value) => UpdateRowLocks();

    partial void OnIsBusyChanged(bool value) => UpdateRowLocks();

    // ----------------------------------------------------------------------------------------------------------
    // Commands
    // ----------------------------------------------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanMutate))]
    private Task EnableAllAsync() => SetAllAsync(enabled: true);

    [RelayCommand(CanExecute = nameof(CanMutate))]
    private Task DisableAllAsync() => SetAllAsync(enabled: false);

    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task ImportAsync(ModRowViewModel? row)
    {
        if (row is null || !row.IsUnmanaged)
        {
            return;
        }

        await RunBusyAsync($"Importing {row.DisplayName}...", async () =>
        {
            var imported = await RunModOperationAsync($"Could not import {row.DisplayName}", async () =>
            {
                var entry = await _mods.ImportUnmanagedAsync(row.FolderName);
                _logger.LogInformation("Imported unmanaged folder {Folder} as {Name} ({Id})", row.FolderName, entry.DisplayName, entry.Id);
            });

            if (!imported)
            {
                _logger.LogDebug("Import of {Folder} did not complete", row.FolderName);
            }
        });
    }

    private bool CanImport(ModRowViewModel? row) => CanMutate && row is { IsUnmanaged: true };

    [RelayCommand(CanExecute = nameof(CanRename))]
    private async Task RenameAsync(ModRowViewModel? row)
    {
        if (row is null || row.IsUnmanaged)
        {
            return;
        }

        var previous = row.DisplayName;
        var input = await _dialogs.PromptAsync("Rename mod", "Display name", previous, "Rename");
        var name = input?.Trim();
        if (string.IsNullOrEmpty(name) || string.Equals(name, previous, StringComparison.Ordinal) || !await EnsureCanMutateAsync())
        {
            return;
        }

        var renamed = await RunModOperationAsync("Could not rename the mod", () => _mods.RenameAsync(row.Id, name));
        if (renamed)
        {
            // The service's Changed event has usually refreshed the row already; this covers the case where it has not yet.
            _logger.LogInformation("Renamed mod {Id} from {Old} to {New}", row.Id, previous, name);
            row.DisplayName = name;
            ApplyFilter();
        }
    }

    private bool CanRename(ModRowViewModel? row) => CanMutate && row is { IsManaged: true };

    [RelayCommand(CanExecute = nameof(CanUninstall))]
    private async Task UninstallAsync(ModRowViewModel? row)
    {
        if (row is null || row.IsUnmanaged)
        {
            return;
        }

        if (row.IsMissing)
        {
            // Nothing on disk to delete: only the manifest entry goes.
            await RemoveMissingAsync(row);
            return;
        }

        var confirmed = await _dialogs.ConfirmAsync(
            $"Uninstall {row.DisplayName}?",
            "The mod folder and its files will be deleted. This cannot be undone.",
            "Uninstall",
            isDestructive: true);
        if (!confirmed || !await EnsureCanMutateAsync())
        {
            return;
        }

        await RunBusyAsync($"Uninstalling {row.DisplayName}...", async () =>
        {
            var removed = await RunModOperationAsync($"Could not uninstall {row.DisplayName}", () => _mods.UninstallAsync(row.Id));
            if (removed)
            {
                _logger.LogInformation("Uninstalled mod {Name} ({Id})", row.DisplayName, row.Id);
            }
        });
    }

    private bool CanUninstall(ModRowViewModel? row) => CanMutate && row is { IsManaged: true };

    [RelayCommand(CanExecute = nameof(CanRemoveMissing))]
    private async Task RemoveMissingAsync(ModRowViewModel? row)
    {
        if (row is null || !row.IsMissing)
        {
            return;
        }

        var confirmed = await _dialogs.ConfirmAsync(
            $"Remove {row.DisplayName}?",
            "The mod folder is already gone. This removes the remaining entry from your mod list.",
            "Remove");
        if (!confirmed || !await EnsureCanMutateAsync())
        {
            return;
        }

        var removed = await RunModOperationAsync($"Could not remove {row.DisplayName}", () => _mods.UninstallAsync(row.Id));
        if (removed)
        {
            _logger.LogInformation("Removed missing mod entry {Name} ({Id})", row.DisplayName, row.Id);
        }
    }

    private bool CanRemoveMissing(ModRowViewModel? row) => CanMutate && row is { IsMissing: true };

    [RelayCommand(CanExecute = nameof(CanOpenFolder))]
    private void OpenFolder(ModRowViewModel? row)
    {
        if (row?.FolderPath is { } path)
        {
            _windows.OpenFolder(path);
        }
    }

    private bool CanOpenFolder(ModRowViewModel? row) => row is { FolderPath: not null };

    [RelayCommand(CanExecute = nameof(CanOpenOnNexus))]
    private void OpenOnNexus(ModRowViewModel? row)
    {
        if (row is { IsNexus: true, NexusModId: { } modId })
        {
            _windows.OpenUrl(NexusConstants.ModPageUrl(modId));
        }
    }

    private bool CanOpenOnNexus(ModRowViewModel? row) => row is { IsNexus: true, NexusModId: not null };

    /// <summary>Placeholder until the Nexus update check (milestone 5); never executable.</summary>
    [RelayCommand(CanExecute = nameof(CanCheckForUpdate))]
    private void CheckForUpdate(ModRowViewModel? row)
    {
    }

    private bool CanCheckForUpdate(ModRowViewModel? row) => false;

    /// <summary>
    /// Moves one mod between <c>~mods</c> and the disabled folder. Called by the row when its switch is flipped;
    /// returns false when the move failed (the row then reverts its switch). Access-denied errors offer the
    /// one-time permission fix and retry once.
    /// </summary>
    public async Task<bool> ToggleAsync(ModRowViewModel row, bool enabled)
    {
        if (row.IsUnmanaged || row.IsMissing)
        {
            return false;
        }

        if (!CanMutate)
        {
            _logger.LogDebug("Ignored toggle of {Name}: mod changes are locked", row.DisplayName);
            return false;
        }

        row.IsBusy = true;
        try
        {
            var moved = await RunModOperationAsync(
                enabled ? $"Could not enable {row.DisplayName}" : $"Could not disable {row.DisplayName}",
                () => _mods.SetEnabledAsync(row.Id, enabled));

            if (moved)
            {
                _logger.LogInformation("{Action} mod {Name} ({Id})", enabled ? "Enabled" : "Disabled", row.DisplayName, row.Id);
            }

            return moved;
        }
        finally
        {
            row.IsBusy = false;

            // Snapshots that arrived while the row was busy were not applied to its switch; sync it now.
            if (_mods.Find(row.Id) is { } current)
            {
                row.Refresh(current);
                UpdateCounts();
            }
        }
    }

    // ----------------------------------------------------------------------------------------------------------
    // Bulk operations and error handling
    // ----------------------------------------------------------------------------------------------------------

    private async Task SetAllAsync(bool enabled)
    {
        var verb = enabled ? "Enabling" : "Disabling";
        await RunBusyAsync($"{verb} all mods...", async () =>
        {
            var failures = await _mods.SetAllEnabledAsync(enabled);

            var denied = failures.Select(f => f.Error).OfType<ModAccessDeniedException>().FirstOrDefault();
            var anyDenied = denied is not null || failures.Any(f => f.Error is UnauthorizedAccessException);
            if (anyDenied && await TryFixPermissionsAsync(denied?.Path))
            {
                failures = await _mods.SetAllEnabledAsync(enabled);
            }

            if (failures.Count == 0)
            {
                _logger.LogInformation("{Action} all mods", enabled ? "Enabled" : "Disabled");
                return;
            }

            foreach (var failure in failures)
            {
                _logger.LogWarning(failure.Error, "Could not {Action} mod {Name} ({Id})", enabled ? "enable" : "disable", failure.Mod.DisplayName, failure.Mod.Id);
            }

            var action = enabled ? "enabled" : "disabled";
            var names = string.Join(Environment.NewLine, failures.Select(f => "• " + f.Mod.DisplayName));
            var message = $"These mods could not be {action}:{Environment.NewLine}{names}{Environment.NewLine}{Environment.NewLine}If the game is running, close it and try again.";
            var details = string.Join(Environment.NewLine + Environment.NewLine, failures.Select(f => $"{f.Mod.FolderName}: {f.Error}"));
            await _dialogs.ShowErrorAsync($"Some mods could not be {action}", message, details);
        });
    }

    /// <summary>Runs a long operation with the busy indicator and the list locked; unexpected errors become a dialog.</summary>
    private async Task RunBusyAsync(string busyText, Func<Task> work)
    {
        if (IsBusy)
        {
            return;
        }

        BusyText = busyText;
        IsBusy = true;
        try
        {
            await work();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Operation failed: {Operation}", busyText);
            await _dialogs.ShowErrorAsync("Something went wrong", "The operation did not complete. See the log for details.", ex.ToString());
        }
        finally
        {
            IsBusy = false;
            BusyText = string.Empty;
        }
    }

    /// <summary>
    /// Runs one mod-store operation and turns its failures into friendly dialogs. Access-denied errors offer the
    /// elevated permission fix and retry the operation once. Returns true when the operation completed.
    /// </summary>
    private async Task<bool> RunModOperationAsync(string failureTitle, Func<Task> operation)
    {
        var retriedAfterPermissionFix = false;
        while (true)
        {
            try
            {
                await operation();
                return true;
            }
            catch (Exception ex) when (ex is ModAccessDeniedException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "{Title}: access denied", failureTitle);
                if (!retriedAfterPermissionFix && await TryFixPermissionsAsync((ex as ModAccessDeniedException)?.Path))
                {
                    retriedAfterPermissionFix = true;
                    continue;
                }

                await _dialogs.ShowErrorAsync(failureTitle, AccessDeniedMessage, ex.ToString());
                return false;
            }
            catch (Exception ex) when (ex is ModNotFoundException or ModOperationException or InvalidOperationException)
            {
                // The mod store phrases these for the user ("folder is in use", "already managed", "not found").
                _logger.LogWarning(ex, "{Title}: {Reason}", failureTitle, ex.Message);
                await _dialogs.ShowErrorAsync(failureTitle, ex.Message, ex.ToString());
                return false;
            }
            catch (IOException ex)
            {
                // Raw file-system text ("The process cannot access the file ...") is not for the user.
                _logger.LogWarning(ex, "{Title}: {Reason}", failureTitle, ex.Message);
                await _dialogs.ShowErrorAsync(
                    failureTitle,
                    "The mod files could not be changed. Close the game and any program that is using them, then try again.",
                    ex.ToString());
                return false;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "{Title}: unexpected error", failureTitle);
                await _dialogs.ShowErrorAsync(failureTitle, "Something went wrong while changing the mod. See the log for details.", ex.ToString());
                return false;
            }
        }
    }

    /// <summary>Offers the one-time elevated permission fix; true when it ran successfully.</summary>
    private Task<bool> TryFixPermissionsAsync(string? deniedPath) => _initializer.TryFixPermissionsAsync(deniedPath);

    // ----------------------------------------------------------------------------------------------------------
    // Rows
    // ----------------------------------------------------------------------------------------------------------

    private void OnModsChanged(object? sender, EventArgs e) => OnUiThread(RebuildRows);

    private void OnGameRunningChanged(object? sender, bool running) => OnUiThread(() => IsGameRunning = running);

    private void OnSettingsSaved(object? sender, EventArgs e) => OnUiThread(() => ActiveProfile = _settings.Current.ActiveProfile);

    /// <summary>Syncs <see cref="Rows"/> with the service, reusing row instances so nothing flickers or jumps.</summary>
    private void RebuildRows()
    {
        var mods = _mods.Mods;
        var unmanaged = _mods.UnmanagedFolders;
        var modsDirectory = _game.Current?.ModsDirectory;

        var error = _mods.InitializationError;
        ShowInitError = error is not null && _game.Current is not null;
        InitErrorText = error switch
        {
            null => string.Empty,
            ModAccessDeniedException denied => $"Windows did not allow {AppInfo.DisplayName} to use the mod folders in {denied.Path}.",
            _ => "The mod list could not be loaded. Make sure the game folder and the app data folder are accessible.",
        };

        var desired = new List<ModRowViewModel>(mods.Count + unmanaged.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var info in mods)
        {
            var key = ModRowViewModel.KeyFor(info.Entry.Id);
            if (_rowsByKey.TryGetValue(key, out var row))
            {
                row.Refresh(info);
            }
            else
            {
                row = new ModRowViewModel(info, ToggleAsync);
                _rowsByKey[key] = row;
            }

            desired.Add(row);
            seen.Add(key);
        }

        foreach (var folder in unmanaged)
        {
            var key = ModRowViewModel.KeyFor(folder);
            if (!_rowsByKey.TryGetValue(key, out var row))
            {
                row = ModRowViewModel.ForUnmanaged(folder, modsDirectory is null ? null : Path.Combine(modsDirectory, folder));
                _rowsByKey[key] = row;
            }

            desired.Add(row);
            seen.Add(key);
        }

        foreach (var stale in _rowsByKey.Keys.Where(key => !seen.Contains(key)).ToList())
        {
            _rowsByKey.Remove(stale);
        }

        var locked = !CanMutate;
        foreach (var row in desired)
        {
            row.IsLocked = locked;
        }

        SyncCollection(Rows, desired);
        UpdateCounts();
        ApplyFilter();
    }

    private void UpdateCounts()
    {
        // Unmanaged folders sit inside ~mods, so the game loads them: they count as enabled.
        TotalCount = Rows.Count;
        EnabledCount = Rows.Count(row => row.IsUnmanaged || (row.IsEnabled && !row.IsMissing));
        HasAnyRows = Rows.Count > 0;
    }

    private void ApplyFilter()
    {
        var query = SearchText.Trim();
        var filter = Filter;
        var visible = Rows.Where(row => MatchesFilter(row, filter) && MatchesSearch(row, query)).ToList();

        SyncCollection(VisibleRows, visible);
        ShowNoMatches = HasAnyRows && visible.Count == 0;
    }

    private static bool MatchesFilter(ModRowViewModel row, ModFilter filter) => filter switch
    {
        ModFilter.Enabled => row.IsUnmanaged || (row.IsEnabled && !row.IsMissing),
        ModFilter.Disabled => row.IsManaged && (!row.IsEnabled || row.IsMissing),
        ModFilter.UpdatesAvailable => false,
        _ => true,
    };

    private static bool MatchesSearch(ModRowViewModel row, string query) =>
        query.Length == 0
        || row.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
        || row.FolderName.Contains(query, StringComparison.OrdinalIgnoreCase)
        || row.Author.Contains(query, StringComparison.OrdinalIgnoreCase);

    private void UpdateRowLocks()
    {
        var locked = !CanMutate;
        foreach (var row in Rows)
        {
            row.IsLocked = locked;
        }
    }

    /// <summary>
    /// Makes <paramref name="target"/> equal to <paramref name="desired"/> with the fewest collection changes
    /// (move / insert / trailing removes), so the ItemsControl keeps its containers and nothing flickers.
    /// </summary>
    private static void SyncCollection<T>(ObservableCollection<T> target, IReadOnlyList<T> desired)
        where T : class
    {
        for (var i = 0; i < desired.Count; i++)
        {
            var item = desired[i];
            if (i < target.Count && ReferenceEquals(target[i], item))
            {
                continue;
            }

            var existing = -1;
            for (var j = i + 1; j < target.Count; j++)
            {
                if (ReferenceEquals(target[j], item))
                {
                    existing = j;
                    break;
                }
            }

            if (existing >= 0)
            {
                target.Move(existing, i);
            }
            else
            {
                target.Insert(i, item);
            }
        }

        while (target.Count > desired.Count)
        {
            target.RemoveAt(target.Count - 1);
        }
    }

    /// <summary>Runs on the UI thread (services raise their events on arbitrary threads).</summary>
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
