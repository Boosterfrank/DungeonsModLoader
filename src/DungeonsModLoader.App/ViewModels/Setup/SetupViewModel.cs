using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DungeonsModLoader.App.Services;
using DungeonsModLoader.Core;
using DungeonsModLoader.Core.Game;
using DungeonsModLoader.Core.Mods;
using DungeonsModLoader.Core.Permissions;
using DungeonsModLoader.Core.Settings;
using Microsoft.Extensions.Logging;

namespace DungeonsModLoader.App.ViewModels.Setup;

/// <summary>
/// First-run setup wizard: 1. find the game, 2. import mods already in <c>~mods</c>, 3. done.
/// The window shows this modally; <see cref="CloseRequested"/> carries the dialog result (true = finished,
/// false = cancelled; the app cannot run without a game folder, so cancelling closes the app).
/// </summary>
public sealed partial class SetupViewModel : ObservableObject, IDisposable
{
    private readonly IGameLocator _locator;
    private readonly IGameContext _gameContext;
    private readonly ISettingsStore _settings;
    private readonly IModService _mods;
    private readonly IPermissionFixer _permissions;
    private readonly IDialogService _dialogs;
    private readonly AppPaths _paths;
    private readonly ILogger<SetupViewModel> _logger;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _closing;
    private bool _disposed;

    public SetupViewModel(
        IGameLocator locator,
        IGameContext gameContext,
        ISettingsStore settings,
        IModService mods,
        IPermissionFixer permissions,
        IDialogService dialogs,
        AppPaths paths,
        ILogger<SetupViewModel> logger)
    {
        _locator = locator;
        _gameContext = gameContext;
        _settings = settings;
        _mods = mods;
        _permissions = permissions;
        _dialogs = dialogs;
        _paths = paths;
        _logger = logger;

        Steps =
        [
            new SetupStepItem(SetupStep.GameFolder, "Game folder"),
            new SetupStepItem(SetupStep.ExistingMods, "Existing mods"),
            new SetupStepItem(SetupStep.Done, "Done"),
        ];
        UpdateStepStates(CurrentStep);

        Candidates.CollectionChanged += (_, _) => OnPropertyChanged(nameof(NothingFound));
    }

    /// <summary>Raised once with the dialog result when the wizard wants its window closed.</summary>
    public event EventHandler<bool>? CloseRequested;

    public string WindowTitle => $"{AppInfo.DisplayName} setup";

    public IReadOnlyList<SetupStepItem> Steps { get; }

    public ObservableCollection<InstallCandidateViewModel> Candidates { get; } = new();

    public ObservableCollection<UnmanagedFolderViewModel> UnmanagedFolders { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGameFolderStep), nameof(IsExistingModsStep), nameof(IsDoneStep))]
    [NotifyPropertyChangedFor(nameof(StepTitle), nameof(StepDescription), nameof(NextText), nameof(ShowBack), nameof(ShowCancel))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand), nameof(BackCommand))]
    private SetupStep _currentStep = SetupStep.GameFolder;

    /// <summary>True while <see cref="IGameLocator.LocateAsync"/> runs (step 1 progress bar).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NothingFound))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand), nameof(BrowseCommand))]
    private bool _isSearching;

    /// <summary>True while a Next / Finish action is doing file work; navigation is locked meanwhile.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextCommand), nameof(BackCommand), nameof(CancelCommand), nameof(BrowseCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string? _busyText;

    /// <summary>Inline error under the Browse button (step 1); null hides it.</summary>
    [ObservableProperty]
    private string? _browseError;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    private InstallCandidateViewModel? _selectedCandidate;

    /// <summary>The installation applied when leaving step 1 (shown in the summary on step 3).</summary>
    [ObservableProperty]
    private InstallCandidateViewModel? _appliedInstallation;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoUnmanagedFolders))]
    private bool _hasUnmanagedFolders;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ImportedSummary))]
    private int _importedCount;

    public bool IsGameFolderStep => CurrentStep == SetupStep.GameFolder;
    public bool IsExistingModsStep => CurrentStep == SetupStep.ExistingMods;
    public bool IsDoneStep => CurrentStep == SetupStep.Done;

    public bool ShowBack => CurrentStep != SetupStep.GameFolder;
    public bool ShowCancel => CurrentStep != SetupStep.Done;

    public bool NothingFound => !IsSearching && Candidates.Count == 0;
    public bool NoUnmanagedFolders => !HasUnmanagedFolders;

    public string StepTitle => CurrentStep switch
    {
        SetupStep.GameFolder => "Find your game",
        SetupStep.ExistingMods => "Existing mods",
        _ => "All set",
    };

    public string StepDescription => CurrentStep switch
    {
        SetupStep.GameFolder => $"We'll look for {AppInfo.GameDisplayName} on this PC. Pick the install you want to manage mods for.",
        SetupStep.ExistingMods => "These folders are already in ~mods. Import them to manage them here; nothing is moved or deleted.",
        _ => $"{AppInfo.DisplayName} is ready. Here's what was set up.",
    };

    public string NextText => CurrentStep == SetupStep.Done ? "Finish" : "Next";

    public string ImportedSummary => ImportedCount switch
    {
        0 => "No mods imported",
        1 => "1 mod imported",
        var n => $"{n} mods imported",
    };

    /// <summary>Called by the window once it is shown: runs game detection for step 1.</summary>
    public async Task StartAsync()
    {
        if (IsSearching || Candidates.Count > 0)
        {
            return;
        }

        IsSearching = true;
        try
        {
            var found = await _locator.LocateAsync(_lifetime.Token);
            _logger.LogInformation("Game detection found {Count} installation(s)", found.Count);
            foreach (var installation in found)
            {
                AddCandidate(installation, select: false);
            }

            if (SelectedCandidate is null && Candidates.Count > 0)
            {
                Candidates[0].IsSelected = true;
            }
        }
        catch (OperationCanceledException)
        {
            // Window closed during detection.
        }
        catch (Exception ex)
        {
            // The locator promises not to throw; if it does anyway the user can still browse to the folder.
            _logger.LogError(ex, "Game detection failed");
        }
        finally
        {
            IsSearching = false;
        }
    }

    private bool CanBrowse() => !IsBusy && !IsSearching;

    [RelayCommand(CanExecute = nameof(CanBrowse))]
    private async Task BrowseAsync()
    {
        BrowseError = null;
        var folder = _dialogs.PickFolder($"Select the {AppInfo.GameDisplayName} folder", SelectedCandidate?.Root);
        if (folder is null)
        {
            return;
        }

        GameInstallation? identified = null;
        try
        {
            identified = await _locator.IdentifyAsync(folder, _lifetime.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Identifying the picked folder {Folder} failed", folder);
        }

        if (identified is null)
        {
            _logger.LogInformation("Picked folder {Folder} is not a game root", folder);
            BrowseError = @"That folder doesn't contain Dungeons\Content\Paks.";
            return;
        }

        _logger.LogInformation("Picked folder {Folder} identified as {Source} install", folder, identified.Source);
        AddCandidate(identified, select: true);
    }

    private bool CanGoNext() =>
        !IsBusy && !IsSearching && (CurrentStep != SetupStep.GameFolder || SelectedCandidate is not null);

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private async Task NextAsync()
    {
        switch (CurrentStep)
        {
            case SetupStep.GameFolder:
                if (await ApplyGameFolderAsync())
                {
                    LoadUnmanagedFolders();
                    CurrentStep = SetupStep.ExistingMods;
                }

                break;

            case SetupStep.ExistingMods:
                await ImportSelectedAsync();
                CurrentStep = SetupStep.Done;
                break;

            case SetupStep.Done:
                await FinishAsync();
                break;
        }
    }

    private bool CanGoBack() => !IsBusy && CurrentStep != SetupStep.GameFolder;

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void Back()
    {
        switch (CurrentStep)
        {
            case SetupStep.ExistingMods:
                CurrentStep = SetupStep.GameFolder;
                break;

            case SetupStep.Done:
                // Folders imported on the way forward are managed now; re-read what is still unmanaged.
                LoadUnmanagedFolders();
                CurrentStep = SetupStep.ExistingMods;
                break;
        }
    }

    private bool CanCancel() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private async Task CancelAsync()
    {
        if (_closing)
        {
            return;
        }

        var exit = await _dialogs.ConfirmAsync("Exit setup?", "The app cannot run without a game folder.", "Exit setup", "Keep going");
        if (exit)
        {
            _logger.LogInformation("First-run setup cancelled on step {Step}", CurrentStep);
            RequestClose(false);
        }
    }

    // ------------------------------------------------------------------------------------------------------
    // Step 1: game folder
    // ------------------------------------------------------------------------------------------------------

    private void AddCandidate(GameInstallation installation, bool select)
    {
        var existing = Candidates.FirstOrDefault(c => SameRoot(c.Root, installation.Root));
        if (existing is null)
        {
            existing = new InstallCandidateViewModel(installation, OnCandidateSelected);
            Candidates.Add(existing);
        }

        if (select)
        {
            existing.IsSelected = true;
        }
    }

    /// <summary>
    /// Keeps the cards mutually exclusive here rather than relying on the RadioButton group: a card selected from
    /// code before its container exists would otherwise leave the previous card checked.
    /// </summary>
    private void OnCandidateSelected(InstallCandidateViewModel candidate)
    {
        SelectedCandidate = candidate;
        foreach (var other in Candidates)
        {
            if (!ReferenceEquals(other, candidate))
            {
                other.IsSelected = false;
            }
        }
    }

    private static bool SameRoot(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Stores the selection (without completing first run), points the game context at it and prepares the mod folders.</summary>
    private async Task<bool> ApplyGameFolderAsync()
    {
        var candidate = SelectedCandidate;
        if (candidate is null)
        {
            return false;
        }

        var installation = candidate.Installation;
        IsBusy = true;
        BusyText = "Preparing the mod folders…";
        try
        {
            _settings.Current.ApplyGameInstallation(installation);
            _gameContext.Set(installation);
            _logger.LogInformation("Setup selected {Source} install at {Root}", installation.Source, installation.Root);

            if (!await InitializeModsAsync(installation))
            {
                return false;
            }

            AppliedInstallation = candidate;
            return true;
        }
        finally
        {
            IsBusy = false;
            BusyText = null;
        }
    }

    /// <summary>
    /// Runs <see cref="IModService.InitializeAsync"/>; on an access-denied error offers the one-time permission fix
    /// and retries once. Any other failure is shown and keeps the user on the step.
    /// </summary>
    private async Task<bool> InitializeModsAsync(GameInstallation installation)
    {
        try
        {
            await _mods.InitializeAsync(_lifetime.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (ModAccessDeniedException denied)
        {
            _logger.LogWarning(denied, "Access denied while preparing the mod folders at {Path}", denied.Path);
            var fix = await _dialogs.ConfirmAsync(
                "Permission needed",
                $"Windows did not allow changes in {denied.Path}. Grant yourself modify rights on the mod folders (one-time, asks for administrator approval)?",
                "Fix permissions");
            if (!fix)
            {
                return false;
            }

            return await FixPermissionsAndRetryAsync(installation);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Preparing the mod folders failed for {Root}", installation.Root);
            await _dialogs.ShowErrorAsync(
                "Could not prepare the mod folders",
                $"The mod folders inside {installation.Root} could not be created or read. Make sure the folder still exists and is on a drive you can write to, then try again.",
                ex.ToString());
            return false;
        }
    }

    private async Task<bool> FixPermissionsAndRetryAsync(GameInstallation installation)
    {
        try
        {
            if (!await _permissions.GrantModifyAccessAsync(installation, _lifetime.Token))
            {
                _logger.LogInformation("Permission fix cancelled by the user");
                return false;
            }

            await _mods.InitializeAsync(_lifetime.Token);
            _logger.LogInformation("Mod folders prepared after the permission fix");
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (PermissionFixException ex)
        {
            _logger.LogError(ex, "The permission fix failed");
            await _dialogs.ShowErrorAsync("Permissions could not be fixed", ex.Message, ex.ToString());
            return false;
        }
        catch (ModAccessDeniedException ex)
        {
            _logger.LogError(ex, "Access still denied after the permission fix at {Path}", ex.Path);
            await _dialogs.ShowErrorAsync(
                "Still no access",
                $"Windows still refuses changes in {ex.Path}. Try running the game once, or pick a different installation.",
                ex.ToString());
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Preparing the mod folders failed after the permission fix");
            await _dialogs.ShowErrorAsync(
                "Could not prepare the mod folders",
                $"The mod folders inside {installation.Root} could not be created or read.",
                ex.ToString());
            return false;
        }
    }

    // ------------------------------------------------------------------------------------------------------
    // Step 2: existing mods
    // ------------------------------------------------------------------------------------------------------

    private void LoadUnmanagedFolders()
    {
        UnmanagedFolders.Clear();

        var modsDirectory = _gameContext.Current?.ModsDirectory;
        foreach (var name in _mods.UnmanagedFolders)
        {
            var path = modsDirectory is null ? name : Path.Combine(modsDirectory, name);
            var row = new UnmanagedFolderViewModel(name, path);
            UnmanagedFolders.Add(row);
            _ = row.LoadSummaryAsync(_lifetime.Token);
        }

        HasUnmanagedFolders = UnmanagedFolders.Count > 0;
        _logger.LogInformation("Setup lists {Count} unmanaged folder(s) in ~mods", UnmanagedFolders.Count);
    }

    /// <summary>Imports every checked folder one after another; failures are collected and reported once.</summary>
    private async Task ImportSelectedAsync()
    {
        var selected = UnmanagedFolders.Where(f => f.IsSelected).ToList();
        if (selected.Count == 0)
        {
            return;
        }

        var failures = new List<(string Name, Exception Error)>();
        var imported = 0;

        IsBusy = true;
        try
        {
            foreach (var row in selected)
            {
                BusyText = $"Importing {row.FolderName}…";
                try
                {
                    await _mods.ImportUnmanagedAsync(row.FolderName, cancellationToken: _lifetime.Token);
                    imported++;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Importing {Folder} failed", row.FolderName);
                    failures.Add((row.FolderName, ex));
                }
            }
        }
        finally
        {
            IsBusy = false;
            BusyText = null;
        }

        ImportedCount += imported;
        _logger.LogInformation("Imported {Imported} of {Selected} existing mod folder(s)", imported, selected.Count);

        if (failures.Count > 0)
        {
            var names = string.Join(", ", failures.Select(f => f.Name));
            var details = string.Join(
                Environment.NewLine + Environment.NewLine,
                failures.Select(f => $"{f.Name}:{Environment.NewLine}{f.Error}"));
            await _dialogs.ShowErrorAsync(
                "Some mods were not imported",
                $"{failures.Count} of {selected.Count} folders could not be imported: {names}. They stay in ~mods untouched; you can import them later from the Installed page.",
                details);
        }
    }

    // ------------------------------------------------------------------------------------------------------
    // Step 3: finish
    // ------------------------------------------------------------------------------------------------------

    private async Task FinishAsync()
    {
        IsBusy = true;
        BusyText = "Saving…";
        try
        {
            _settings.Current.FirstRunCompleted = true;
            await _settings.SaveAsync(_lifetime.Token);
            _logger.LogInformation("First-run setup completed for {Root} ({Source}); {Imported} mod(s) imported",
                _settings.Current.GameRootPath, _settings.Current.GameSource, ImportedCount);
            RequestClose(true);
        }
        catch (OperationCanceledException)
        {
            // Window closed meanwhile.
        }
        catch (Exception ex)
        {
            _settings.Current.FirstRunCompleted = false;
            _logger.LogError(ex, "Saving settings failed at the end of setup");
            await _dialogs.ShowErrorAsync(
                "Settings could not be saved",
                $"Your choices could not be written to {_paths.SettingsFile}. Make sure the folder is writable and try again.",
                ex.ToString());
        }
        finally
        {
            IsBusy = false;
            BusyText = null;
        }
    }

    private void RequestClose(bool result)
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        CloseRequested?.Invoke(this, result);
    }

    partial void OnCurrentStepChanged(SetupStep value) => UpdateStepStates(value);

    private void UpdateStepStates(SetupStep current)
    {
        foreach (var step in Steps)
        {
            step.IsCurrent = step.Step == current;
            step.IsCompleted = step.Step < current;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        // Cancel only: background summary tasks may still touch the token after the window is gone.
        _lifetime.Cancel();
    }
}
