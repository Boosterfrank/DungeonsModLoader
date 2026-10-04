using System.IO;
using DungeonsModLoader.Core;
using DungeonsModLoader.Core.Game;
using DungeonsModLoader.Core.Install;
using DungeonsModLoader.Core.Mods;
using Microsoft.Extensions.Logging;

namespace DungeonsModLoader.App.Services;

/// <inheritdoc cref="IInstallCoordinator"/>
public sealed class InstallCoordinator : IInstallCoordinator
{
    private const string ChoiceReplace = "replace";
    private const string ChoiceKeepBoth = "keep-both";
    private const string ChoiceCancel = "cancel";

    private readonly IModInstaller _installer;
    private readonly IModService _mods;
    private readonly IGameContext _game;
    private readonly IGameProcessMonitor _monitor;
    private readonly IDialogService _dialogs;
    private readonly IModStoreInitializer _initializer;
    private readonly ILogger<InstallCoordinator> _logger;

    public InstallCoordinator(
        IModInstaller installer,
        IModService mods,
        IGameContext game,
        IGameProcessMonitor monitor,
        IDialogService dialogs,
        IModStoreInitializer initializer,
        ILogger<InstallCoordinator> logger)
    {
        _installer = installer;
        _mods = mods;
        _game = game;
        _monitor = monitor;
        _dialogs = dialogs;
        _initializer = initializer;
        _logger = logger;
    }

    public event EventHandler<ModEntry>? Installed;

    /// <summary>True when at least one path could be installed: an archive, a mod file or an existing folder (drag-over feedback).</summary>
    public static bool CanAcceptPaths(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            if (InstallSource.IsArchive(path) || InstallSource.IsModFile(path))
            {
                return true;
            }

            try
            {
                if (Directory.Exists(path))
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Not a usable path; keep looking.
            }
        }

        return false;
    }

    /// <summary>1 while an install flow (with its dialogs) is running; a second drop meanwhile is refused instead of interleaved.</summary>
    private int _active;

    public async Task<IReadOnlyList<ModEntry>> InstallFromPathsAsync(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var installed = new List<ModEntry>();

        if (Interlocked.CompareExchange(ref _active, 1, 0) != 0)
        {
            _logger.LogInformation("Install request ignored: another install is in progress");
            await _dialogs.ShowInfoAsync("Install in progress", "Finish the current install first, then drop the next package.");
            return installed;
        }

        try
        {
            return await InstallFromPathsCoreAsync(paths, installed);
        }
        finally
        {
            Interlocked.Exchange(ref _active, 0);
        }
    }

    private async Task<IReadOnlyList<ModEntry>> InstallFromPathsCoreAsync(IEnumerable<string> paths, List<ModEntry> installed)
    {
        if (_game.Current is null)
        {
            await _dialogs.ShowErrorAsync("Game folder not set", $"Choose your {AppInfo.GameDisplayName} folder in Settings before installing mods.");
            return installed;
        }

        if (!await EnsureGameNotRunningAsync())
        {
            return installed;
        }

        if (_mods.InitializationError is not null)
        {
            _logger.LogInformation("Install refused: the mod store is not ready ({Error})", _mods.InitializationError.Message);
            await _dialogs.ShowErrorAsync(
                "Mod list unavailable",
                "The mod list could not be prepared, so nothing can be installed right now. Use \"Try again\" on the Installed page first.");
            return installed;
        }

        var sources = InstallSource.FromPaths(paths, out var ignored);
        if (ignored.Count > 0)
        {
            _logger.LogInformation("Ignored {Count} path(s) that are not mod packages: {Paths}", ignored.Count, ignored);
        }

        if (sources.Count == 0)
        {
            if (ignored.Count > 0)
            {
                await _dialogs.ShowErrorAsync("Nothing to install", "Drop a .zip, .7z or .rar archive, or .pak/.ucas/.utoc files.");
            }

            return installed;
        }

        _logger.LogInformation("Installing {Count} source(s): {Sources}", sources.Count, sources.Select(s => s.DisplayName));
        foreach (var source in sources)
        {
            var entry = await InstallOneAsync(source);
            if (entry is not null)
            {
                installed.Add(entry);
            }
        }

        return installed;
    }

    /// <summary>Inspects, lets the user choose, and installs one source. Returns null when cancelled or failed (after telling the user).</summary>
    private async Task<ModEntry?> InstallOneAsync(InstallSource source)
    {
        InstallPlan plan;
        try
        {
            plan = await _dialogs.RunWithProgressAsync(
                $"Preparing {source.DisplayName}",
                (progress, cancellationToken) => _installer.InspectAsync(source, Adapt(progress), cancellationToken));
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Inspection of {Source} cancelled", source.DisplayName);
            return null;
        }
        catch (InstallPackageException ex)
        {
            _logger.LogInformation("{Source} cannot be installed: {Reason}", source.DisplayName, ex.Message);
            await _dialogs.ShowErrorAsync($"Could not install {source.DisplayName}", ex.Message, ex.InnerException?.ToString());
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Inspection of {Source} failed", source.DisplayName);
            await _dialogs.ShowErrorAsync(
                $"Could not install {source.DisplayName}",
                "The package could not be read. Make sure the file is complete and not in use, then try again.",
                ex.ToString());
            return null;
        }

        using (plan)
        {
            if (plan.IsEmpty)
            {
                await _dialogs.ShowErrorAsync($"Could not install {source.DisplayName}", $"No mod files (.pak with .ucas/.utoc) were found in {source.DisplayName}.");
                return null;
            }

            IReadOnlyList<ModFileSet> selected;
            string name;
            if (plan.RequiresChoice)
            {
                var choice = await _dialogs.ShowInstallPickerAsync(plan);
                if (choice is null)
                {
                    _logger.LogInformation("Install of {Source} cancelled in the picker", source.DisplayName);
                    return null;
                }

                selected = choice.Selected;
                name = choice.Name;
            }
            else
            {
                selected = plan.DefaultSelection;
                name = plan.SuggestedName;
            }

            // The game may have started while a dialog was open.
            if (!await EnsureGameNotRunningAsync())
            {
                return null;
            }

            return await InstallWithRetriesAsync(plan, selected, name);
        }
    }

    /// <summary>
    /// Runs the install, resolving a folder conflict through a choice dialog and an access-denied error through
    /// the one-time permission fix; other failures become error dialogs.
    /// </summary>
    private async Task<ModEntry?> InstallWithRetriesAsync(InstallPlan plan, IReadOnlyList<ModFileSet> selected, string name)
    {
        var resolution = ConflictResolution.Ask;
        var permissionFixOffered = false;
        var title = $"Could not install {name}";

        while (true)
        {
            // The game may have started while a conflict or permission dialog was open.
            if (!await EnsureGameNotRunningAsync())
            {
                return null;
            }

            var request = new InstallRequest(plan, selected, name, resolution);
            try
            {
                var entry = await _dialogs.RunWithProgressAsync(
                    $"Installing {name}",
                    (progress, cancellationToken) => _installer.InstallAsync(request, Adapt(progress), cancellationToken));

                if (resolution == ConflictResolution.KeepBoth)
                {
                    entry = await NameCopyAsync(entry, name);
                }

                _logger.LogInformation("Installed '{Mod}' ({Id}) from {Source}", entry.DisplayName, entry.Id, plan.Source.DisplayName);
                Installed?.Invoke(this, entry);
                return entry;
            }
            catch (InstallConflictException conflict)
            {
                var next = await ResolveConflictAsync(conflict);
                if (next is null)
                {
                    _logger.LogInformation("Install of {Source} cancelled at the folder conflict for '{Folder}'", plan.Source.DisplayName, conflict.FolderName);
                    return null;
                }

                resolution = next.Value;
                _logger.LogInformation("Folder conflict for '{Folder}' ({Kind}): retrying with {Resolution}", conflict.FolderName, conflict.Kind, resolution);
            }
            catch (Exception ex) when (ex is ModAccessDeniedException or UnauthorizedAccessException)
            {
                var deniedPath = (ex as ModAccessDeniedException)?.Path;
                _logger.LogWarning(ex, "{Title}: access denied at {Path}", title, deniedPath ?? "(unknown)");
                if (!permissionFixOffered)
                {
                    permissionFixOffered = true;
                    if (await _initializer.TryFixPermissionsAsync(deniedPath))
                    {
                        continue;
                    }
                }

                await _dialogs.ShowErrorAsync(
                    title,
                    "Windows did not allow changes to the mod folder. Fix the folder permissions and try again.",
                    ex.ToString());
                return null;
            }
            catch (Exception ex) when (ex is InstallPackageException or ModOperationException or ModNotFoundException or InvalidOperationException)
            {
                // These are phrased for the user ("Nothing to install", "folder is in use", "no game configured").
                _logger.LogWarning(ex, "{Title}: {Reason}", title, ex.Message);
                await _dialogs.ShowErrorAsync(title, ex.Message, ex.ToString());
                return null;
            }
            catch (IOException ex)
            {
                // Raw file-system text ("The process cannot access the file ...") is not for the user.
                _logger.LogWarning(ex, "{Title}: {Reason}", title, ex.Message);
                await _dialogs.ShowErrorAsync(
                    title,
                    "The mod files could not be copied. Close the game and any program using them, then try again.",
                    ex.ToString());
                return null;
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Install of {Source} cancelled while copying", plan.Source.DisplayName);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{Title}: unexpected error", title);
                await _dialogs.ShowErrorAsync(title, "Something went wrong while installing the mod. See the log for details.", ex.ToString());
                return null;
            }
        }
    }

    /// <summary>
    /// "Keep both" installs into a suffixed folder ("Name (2)"); the copy gets the same suffix in its display name
    /// so the two rows can be told apart. A failed rename is logged and the install still counts.
    /// </summary>
    private async Task<ModEntry> NameCopyAsync(ModEntry entry, string requestedName)
    {
        var baseFolder = FolderNameSanitizer.Sanitize(requestedName.Trim());
        if (entry.FolderName.Length <= baseFolder.Length
            || !entry.FolderName.StartsWith(baseFolder + " (", StringComparison.OrdinalIgnoreCase)
            || !entry.FolderName.EndsWith(')'))
        {
            return entry;
        }

        var copyName = requestedName.Trim() + entry.FolderName[baseFolder.Length..];
        try
        {
            await _mods.RenameAsync(entry.Id, copyName);
            return _mods.Find(entry.Id)?.Entry ?? entry;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "The copy installed into '{Folder}' keeps the name '{Name}'", entry.FolderName, entry.DisplayName);
            return entry;
        }
    }

    /// <summary>Asks how to deal with an existing folder; null means cancel.</summary>
    private async Task<ConflictResolution?> ResolveConflictAsync(InstallConflictException conflict)
    {
        string? choice;
        if (conflict.Kind == InstallConflictKind.ManagedMod)
        {
            var existingName = conflict.ExistingMod?.DisplayName ?? conflict.FolderName;
            choice = await _dialogs.ChooseAsync(
                "Already installed",
                $"'{existingName}' is already installed from the folder '{conflict.FolderName}'. "
                + "Replace it (keeps its name and enabled state), or install this one as a copy?",
                new[]
                {
                    new DialogChoice(ChoiceCancel, "Cancel"),
                    new DialogChoice(ChoiceKeepBoth, "Install as copy"),
                    new DialogChoice(ChoiceReplace, "Replace", IsPrimary: true),
                });
        }
        else
        {
            choice = await _dialogs.ChooseAsync(
                "Folder already exists",
                $"A folder named '{conflict.FolderName}' already exists in ~mods and is not managed by {AppInfo.DisplayName}. "
                + "Replace it, or install next to it?",
                new[]
                {
                    new DialogChoice(ChoiceCancel, "Cancel"),
                    new DialogChoice(ChoiceReplace, "Replace", IsDestructive: true),
                    new DialogChoice(ChoiceKeepBoth, "Keep both", IsPrimary: true),
                });
        }

        return choice switch
        {
            ChoiceReplace => ConflictResolution.Replace,
            ChoiceKeepBoth => ConflictResolution.KeepBoth,
            _ => null,
        };
    }

    private async Task<bool> EnsureGameNotRunningAsync()
    {
        if (!_monitor.IsGameRunning)
        {
            return true;
        }

        _logger.LogInformation("Install refused: the game is running");
        await _dialogs.ShowInfoAsync("Game running", $"{AppInfo.GameDisplayName} is running. Close it before installing mods.");
        return false;
    }

    /// <summary>Forwards the installer's progress to the dialog's progress type.</summary>
    private static IProgress<InstallProgress> Adapt(IProgress<ProgressUpdate> progress) => new ProgressAdapter(progress);

    private sealed class ProgressAdapter : IProgress<InstallProgress>
    {
        private readonly IProgress<ProgressUpdate> _inner;

        public ProgressAdapter(IProgress<ProgressUpdate> inner)
        {
            _inner = inner;
        }

        public void Report(InstallProgress value) => _inner.Report(new ProgressUpdate(value.Message, value.Fraction));
    }
}
