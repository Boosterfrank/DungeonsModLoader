using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DungeonsModLoader.App.Services;
using DungeonsModLoader.Core;
using DungeonsModLoader.Core.Game;
using DungeonsModLoader.Core.Mods;
using DungeonsModLoader.Core.Profiles;
using Microsoft.Extensions.Logging;

namespace DungeonsModLoader.App.ViewModels.Pages;

/// <summary>
/// Profiles page: the list of profiles with create / rename / duplicate / delete / switch / export / import.
/// Rows mirror <see cref="IProfileService.Profiles"/> and are refreshed in place. Switching (which moves mod
/// folders) is locked while the game runs, like every other mod change.
/// </summary>
public sealed partial class ProfilesViewModel : PageViewModel
{
    private const string ProfileFileFilter = "Profile files (*.json)|*.json|All files (*.*)|*.*";

    private readonly IProfileService _profiles;
    private readonly IModService _mods;
    private readonly IGameProcessMonitor _monitor;
    private readonly IDialogService _dialogs;
    private readonly IWindowService _windows;
    private readonly AppPaths _paths;
    private readonly ILogger<ProfilesViewModel> _logger;
    private readonly Dictionary<string, ProfileRowViewModel> _rowsByName = new(StringComparer.OrdinalIgnoreCase);

    public ProfilesViewModel(
        IProfileService profiles,
        IModService mods,
        IGameProcessMonitor monitor,
        IDialogService dialogs,
        IWindowService windows,
        AppPaths paths,
        ILogger<ProfilesViewModel> logger)
    {
        _profiles = profiles;
        _mods = mods;
        _monitor = monitor;
        _dialogs = dialogs;
        _windows = windows;
        _paths = paths;
        _logger = logger;

        _isGameRunning = _monitor.IsGameRunning;
        _profiles.Changed += OnProfilesChanged;
        _mods.Changed += OnProfilesChanged;
        _monitor.GameRunningChanged += OnGameRunningChanged;
        RebuildRows();
    }

    public override string Title => "Profiles";

    public ObservableCollection<ProfileRowViewModel> Rows { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FooterText))]
    private string _activeProfileName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FooterText))]
    private int _profileCount;

    public string FooterText => $"{ProfileCount} {(ProfileCount == 1 ? "profile" : "profiles")} · Active: {ActiveProfileName}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanMutate))]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand), nameof(ImportCommand), nameof(ActivateCommand), nameof(RenameCommand), nameof(DuplicateCommand), nameof(DeleteCommand), nameof(ExportCommand))]
    private bool _isGameRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanMutate))]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand), nameof(ImportCommand), nameof(ActivateCommand), nameof(RenameCommand), nameof(DuplicateCommand), nameof(DeleteCommand), nameof(ExportCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _busyText = string.Empty;

    /// <summary>Profile changes are allowed: no long operation is running. Switching additionally needs the game to be closed.</summary>
    public bool CanMutate => !IsBusy;

    private bool CanSwitch(ProfileRowViewModel? row) => CanMutate && !IsGameRunning && row is { IsActive: false };

    private bool CanAct(ProfileRowViewModel? row) => CanMutate && row is not null;

    private bool CanDeleteRow(ProfileRowViewModel? row) => CanMutate && row is { IsActive: false };

    // ----------------------------------------------------------------------------------------------------------
    // Commands
    // ----------------------------------------------------------------------------------------------------------

    /// <summary>New profile: starts with the mods enabled right now.</summary>
    [RelayCommand(CanExecute = nameof(CanMutate))]
    private async Task CreateAsync()
    {
        var name = await _dialogs.PromptAsync("New profile", "The new profile starts with the mods that are enabled right now.", SuggestName("New profile"), "Create");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        await RunAsync("Creating profile...", async () =>
        {
            var created = await _profiles.CreateAsync(name);
            _logger.LogInformation("Profile '{Name}' created from the Profiles page", created.Name);
        });
    }

    /// <summary>Switch: applies the profile (moves folders) and makes it active.</summary>
    [RelayCommand(CanExecute = nameof(CanSwitch))]
    private Task ActivateAsync(ProfileRowViewModel? row) => row is null ? Task.CompletedTask : SwitchToAsync(row.Name);

    /// <summary>
    /// Applies <paramref name="profileName"/> with a progress dialog. Shared with the Installed page's dropdown.
    /// Returns true when the profile is active afterwards.
    /// </summary>
    public async Task<bool> SwitchToAsync(string profileName)
    {
        if (IsGameRunning)
        {
            await _dialogs.ShowInfoAsync("Game running", $"{AppInfo.GameDisplayName} is running. Close it before switching profiles.");
            return false;
        }

        if (string.Equals(_profiles.ActiveProfileName, profileName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var switched = false;
        await RunAsync($"Switching to {profileName}...", async () =>
        {
            try
            {
                await _dialogs.RunWithProgressAsync<bool>(
                    $"Switching to {profileName}",
                    async (progress, cancellationToken) =>
                    {
                        var adapter = new Progress<string>(message => progress.Report(new ProgressUpdate(message)));
                        await _profiles.SwitchAsync(profileName, adapter, cancellationToken);
                        return true;
                    },
                    canCancel: false);
                switched = true;
                _logger.LogInformation("Switched to profile '{Name}'", profileName);
            }
            catch (ModApplyException ex)
            {
                _logger.LogWarning(ex, "Profile switch to '{Name}' failed at '{Mod}'", profileName, ex.FailedMod.DisplayName);
                await _dialogs.ShowErrorAsync($"Could not switch to {profileName}", ex.Message, ex.ToString());
            }
            catch (ProfileException ex)
            {
                _logger.LogWarning(ex, "Profile switch to '{Name}' refused", profileName);
                await _dialogs.ShowErrorAsync($"Could not switch to {profileName}", ex.Message, ex.ToString());
            }
            catch (Exception ex) when (ex is ModAccessDeniedException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Profile switch to '{Name}' failed: access denied", profileName);
                await _dialogs.ShowErrorAsync($"Could not switch to {profileName}", "Windows did not allow changes to the mod folders. Fix the folder permissions on the Installed page and try again.", ex.ToString());
            }
        });

        return switched;
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task RenameAsync(ProfileRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        var previous = row.Name;
        var name = await _dialogs.PromptAsync("Rename profile", "Profile name", previous, "Rename");
        if (string.IsNullOrWhiteSpace(name) || string.Equals(name.Trim(), previous, StringComparison.Ordinal))
        {
            return;
        }

        await RunAsync("Renaming profile...", async () =>
        {
            await _profiles.RenameAsync(previous, name);
            _logger.LogInformation("Profile '{Old}' renamed to '{New}'", previous, name.Trim());
        });
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task DuplicateAsync(ProfileRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        var name = await _dialogs.PromptAsync("Duplicate profile", $"Name for the copy of '{row.Name}'", SuggestName(row.Name + " copy"), "Duplicate");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        await RunAsync("Duplicating profile...", async () =>
        {
            var copy = await _profiles.DuplicateAsync(row.Name, name);
            _logger.LogInformation("Profile '{Source}' duplicated as '{Copy}'", row.Name, copy.Name);
        });
    }

    [RelayCommand(CanExecute = nameof(CanDeleteRow))]
    private async Task DeleteAsync(ProfileRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        var confirmed = await _dialogs.ConfirmAsync(
            $"Delete {row.Name}?",
            "Only the profile is removed. Your mods and their files stay where they are.",
            "Delete",
            isDestructive: true);
        if (!confirmed)
        {
            return;
        }

        await RunAsync("Deleting profile...", async () =>
        {
            await _profiles.DeleteAsync(row.Name);
            _logger.LogInformation("Profile '{Name}' deleted", row.Name);
        });
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task ExportAsync(ProfileRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        var suggested = SanitizeFileName(row.Name) + ".json";
        var path = _dialogs.PickSaveFile("Export profile", ProfileFileFilter, suggested);
        if (path is null)
        {
            return;
        }

        await RunAsync("Exporting profile...", async () =>
        {
            await _profiles.ExportAsync(row.Name, path);
            _logger.LogInformation("Profile '{Name}' exported to {Path}", row.Name, path);
            await _dialogs.ShowInfoAsync("Profile exported", $"'{row.Name}' was saved to{Environment.NewLine}{path}{Environment.NewLine}{Environment.NewLine}Share this file; others can import it from their Profiles page.");
        });
    }

    [RelayCommand(CanExecute = nameof(CanMutate))]
    private async Task ImportAsync()
    {
        var files = _dialogs.PickFiles("Import profile", ProfileFileFilter, multiSelect: false);
        if (files.Count == 0)
        {
            return;
        }

        await RunAsync("Importing profile...", async () =>
        {
            var result = await _profiles.ImportAsync(files[0]);
            _logger.LogInformation("Profile '{Name}' imported from {Path} ({Missing} mods missing)", result.Profile.Name, files[0], result.MissingMods.Count);

            if (result.MissingMods.Count > 0)
            {
                var names = string.Join(Environment.NewLine, result.MissingMods.Select(m => "• " + m.DisplayName + (m.NexusModId is null ? string.Empty : " (Nexus Mods)")));
                await _dialogs.ShowInfoAsync(
                    $"Imported {result.Profile.Name}",
                    $"The profile was added. {result.MissingMods.Count} of its {result.Profile.ModCount} mods {(result.MissingMods.Count == 1 ? "is" : "are")} not installed on this PC:{Environment.NewLine}{names}{Environment.NewLine}{Environment.NewLine}"
                    + "Install them (Nexus mods can be downloaded from the Browse page once you are logged in), then switch to the profile.");
            }
            else
            {
                var activate = await _dialogs.ConfirmAsync($"Imported {result.Profile.Name}", "All of its mods are installed. Switch to it now?", "Switch now", "Not now");
                if (activate)
                {
                    await SwitchToAsync(result.Profile.Name);
                }
            }
        });
    }

    [RelayCommand]
    private void OpenProfilesFolder() => _windows.OpenFolder(_paths.ProfilesDirectory);

    // ----------------------------------------------------------------------------------------------------------
    // Rows & helpers
    // ----------------------------------------------------------------------------------------------------------

    private void OnProfilesChanged(object? sender, EventArgs e) => OnUiThread(RebuildRows);

    private void OnGameRunningChanged(object? sender, bool running) => OnUiThread(() => IsGameRunning = running);

    private void RebuildRows()
    {
        var desired = new List<ProfileRowViewModel>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var info in _profiles.Profiles)
        {
            var missing = _profiles.FindMissingMods(info.Name).Count;
            if (_rowsByName.TryGetValue(info.Name, out var row))
            {
                row.Refresh(info, missing);
            }
            else
            {
                row = new ProfileRowViewModel(info);
                row.Refresh(info, missing);
                _rowsByName[info.Name] = row;
            }

            desired.Add(row);
            seen.Add(info.Name);
        }

        foreach (var stale in _rowsByName.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _rowsByName.Remove(stale);
        }

        Sync(Rows, desired);
        ProfileCount = Rows.Count;
        ActiveProfileName = _profiles.ActiveProfileName;
        ActivateCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
    }

    private string SuggestName(string baseName)
    {
        var candidate = baseName;
        for (var n = 2; _profiles.Find(candidate) is not null; n++)
        {
            candidate = $"{baseName} {n}";
        }

        return candidate;
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return cleaned.Length == 0 ? "profile" : cleaned;
    }

    /// <summary>Runs an operation with the busy indicator; refused and failed operations become dialogs.</summary>
    private async Task RunAsync(string busyText, Func<Task> work)
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
        catch (ProfileException ex)
        {
            _logger.LogWarning(ex, "Profile operation refused: {Operation}", busyText);
            await _dialogs.ShowErrorAsync("Profile not changed", ex.Message, ex.InnerException?.ToString());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Profile operation failed: {Operation}", busyText);
            await _dialogs.ShowErrorAsync("Something went wrong", "The operation did not complete. See the log for details.", ex.ToString());
        }
        finally
        {
            IsBusy = false;
            BusyText = string.Empty;
        }
    }

    private static void Sync<T>(ObservableCollection<T> target, IReadOnlyList<T> desired)
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
