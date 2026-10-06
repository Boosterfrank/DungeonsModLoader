using System.IO;
using DungeonsModLoader.App.ViewModels;
using DungeonsModLoader.Core;
using DungeonsModLoader.Core.Game;
using DungeonsModLoader.Core.Install;
using DungeonsModLoader.Core.Mods;
using DungeonsModLoader.Nexus.Api;
using DungeonsModLoader.Nexus.Auth;
using DungeonsModLoader.Nexus.Download;
using DungeonsModLoader.Nexus.Nxm;
using DungeonsModLoader.Nexus.Updates;
using Microsoft.Extensions.Logging;

namespace DungeonsModLoader.App.Services;

/// <inheritdoc cref="IInstallCoordinator"/>
public sealed class InstallCoordinator : IInstallCoordinator
{
    private const string ChoiceReplace = "replace";
    private const string ChoiceKeepBoth = "keep-both";
    private const string ChoiceSeparate = "separate";
    private const string ChoiceCancel = "cancel";

    private readonly IModInstaller _installer;
    private readonly IModService _mods;
    private readonly IGameContext _game;
    private readonly IGameProcessMonitor _monitor;
    private readonly IDialogService _dialogs;
    private readonly IModStoreInitializer _initializer;
    private readonly INexusApiClient _client;
    private readonly INexusSession _session;
    private readonly IDownloadService _downloads;
    private readonly IModUpdateChecker _updates;
    private readonly IWindowService _windows;
    private readonly AppPaths _paths;
    private readonly ILogger<InstallCoordinator> _logger;

    /// <summary>1 while an install flow (with its dialogs) is running; a second request meanwhile is refused instead of interleaved.</summary>
    private int _active;

    public InstallCoordinator(
        IModInstaller installer,
        IModService mods,
        IGameContext game,
        IGameProcessMonitor monitor,
        IDialogService dialogs,
        IModStoreInitializer initializer,
        INexusApiClient client,
        INexusSession session,
        IDownloadService downloads,
        IModUpdateChecker updates,
        IWindowService windows,
        AppPaths paths,
        ILogger<InstallCoordinator> logger)
    {
        _installer = installer;
        _mods = mods;
        _game = game;
        _monitor = monitor;
        _dialogs = dialogs;
        _initializer = initializer;
        _client = client;
        _session = session;
        _downloads = downloads;
        _updates = updates;
        _windows = windows;
        _paths = paths;
        _logger = logger;
    }

    public event EventHandler<ModEntry>? Installed;

    public event EventHandler<ModEntry>? Updated;

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

    // ---------------------------------------------------------------------------------------------------------------
    // Entry points (one flow at a time)
    // ---------------------------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<ModEntry>> InstallFromPathsAsync(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var installed = new List<ModEntry>();
        if (!TryEnter())
        {
            await ShowBusyAsync();
            return installed;
        }

        try
        {
            return await InstallFromPathsCoreAsync(paths, installed);
        }
        finally
        {
            Leave();
        }
    }

    public async Task<ModEntry?> InstallFromNexusAsync(NexusMod mod, NexusFile file, NxmLink? link = null)
    {
        ArgumentNullException.ThrowIfNull(mod);
        ArgumentNullException.ThrowIfNull(file);
        if (!TryEnter())
        {
            await ShowBusyAsync();
            return null;
        }

        try
        {
            return await InstallFromNexusCoreAsync(mod, file, link, updateOf: null);
        }
        finally
        {
            Leave();
        }
    }

    public async Task<ModEntry?> UpdateFromNexusAsync(NexusModUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (!TryEnter())
        {
            await ShowBusyAsync();
            return null;
        }

        try
        {
            return await UpdateCoreAsync(update);
        }
        finally
        {
            Leave();
        }
    }

    public async Task<int> UpdateAllFromNexusAsync(IReadOnlyList<NexusModUpdate> updates)
    {
        ArgumentNullException.ThrowIfNull(updates);
        if (!TryEnter())
        {
            await ShowBusyAsync();
            return 0;
        }

        try
        {
            var done = 0;
            foreach (var update in updates)
            {
                if (await UpdateCoreAsync(update) is not null)
                {
                    done++;
                }
            }

            _logger.LogInformation("Update all: {Done} of {Total} updated", done, updates.Count);
            return done;
        }
        finally
        {
            Leave();
        }
    }

    public async Task HandleNxmLinkAsync(NxmLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        if (!TryEnter())
        {
            await ShowBusyAsync();
            return;
        }

        try
        {
            await HandleNxmLinkCoreAsync(link);
        }
        finally
        {
            Leave();
        }
    }

    private bool TryEnter() => Interlocked.CompareExchange(ref _active, 1, 0) == 0;

    private void Leave() => Interlocked.Exchange(ref _active, 0);

    private Task ShowBusyAsync()
    {
        _logger.LogInformation("Install request ignored: another install is in progress");
        return _dialogs.ShowInfoAsync("Install in progress", "Finish the current install first, then try again.");
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Local sources
    // ---------------------------------------------------------------------------------------------------------------

    private async Task<IReadOnlyList<ModEntry>> InstallFromPathsCoreAsync(IEnumerable<string> paths, List<ModEntry> installed)
    {
        if (!await CheckPreconditionsAsync("installing mods"))
        {
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
            var entry = await InstallOneAsync(source, preferredName: null, metadata: null, updateOf: null);
            if (entry is not null)
            {
                installed.Add(entry);
            }
        }

        return installed;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Nexus
    // ---------------------------------------------------------------------------------------------------------------

    private async Task<ModEntry?> InstallFromNexusCoreAsync(NexusMod mod, NexusFile file, NxmLink? link, Guid? updateOf)
    {
        if (!await CheckPreconditionsAsync("installing mods"))
        {
            return null;
        }

        if (updateOf is null)
        {
            var existing = _mods.Mods.FirstOrDefault(m => m.Entry.Source == ModSource.Nexus && m.Entry.NexusModId == mod.ModId);
            if (existing is not null)
            {
                var fileLabel = $"{file.Name} {(string.IsNullOrWhiteSpace(file.Version) ? string.Empty : "v" + file.Version)}".Trim();
                if (existing.Entry.NexusFileId == file.FileId)
                {
                    var again = await _dialogs.ConfirmAsync(
                        "Already installed",
                        $"'{existing.Entry.DisplayName}' already has this file ({fileLabel}). Install it again anyway? The current files are kept as a backup.",
                        "Reinstall");
                    if (!again)
                    {
                        return null;
                    }

                    updateOf = existing.Entry.Id;
                }
                else
                {
                    var choice = await _dialogs.ChooseAsync(
                        "Already installed",
                        $"'{existing.Entry.DisplayName}' is installed from this mod ({existing.Entry.Version ?? "unknown version"}). "
                        + $"Replace it with {fileLabel} (keeps its name, enabled state and profiles), or install this file as a separate mod?",
                        new[]
                        {
                            new DialogChoice(ChoiceCancel, "Cancel"),
                            new DialogChoice(ChoiceSeparate, "Install separately"),
                            new DialogChoice(ChoiceReplace, "Replace", IsPrimary: true),
                        });
                    switch (choice)
                    {
                        case ChoiceReplace:
                            updateOf = existing.Entry.Id;
                            break;
                        case ChoiceSeparate:
                            break;
                        default:
                            return null;
                    }
                }
            }
        }

        return await DownloadAndInstallAsync(mod, file, link, updateOf);
    }

    private async Task<ModEntry?> UpdateCoreAsync(NexusModUpdate update)
    {
        var info = _mods.Find(update.ModId);
        if (info is null)
        {
            await _dialogs.ShowInfoAsync("Mod not installed", "That mod is no longer installed, so there is nothing to update.");
            _updates.Clear(update.ModId);
            return null;
        }

        if (!await CheckPreconditionsAsync("updating mods"))
        {
            return null;
        }

        NexusMod mod;
        try
        {
            mod = await _dialogs.RunWithProgressAsync(
                $"Updating {info.Entry.DisplayName}",
                (progress, cancellationToken) =>
                {
                    progress.Report(new ProgressUpdate("Reading mod information from Nexus Mods..."));
                    return _client.GetModAsync(update.NexusModId, cancellationToken: cancellationToken);
                },
                canCancel: false);
        }
        catch (NexusException ex)
        {
            _logger.LogWarning("Update of '{Mod}' could not start: {Message}", info.Entry.DisplayName, ex.Message);
            await _dialogs.ShowErrorAsync($"Could not update {info.Entry.DisplayName}", ex.Message, ex.ToString());
            return null;
        }

        return await DownloadAndInstallAsync(mod, update.NewFile, link: null, updateOf: update.ModId);
    }

    private async Task HandleNxmLinkCoreAsync(NxmLink link)
    {
        _logger.LogInformation("Handling nxm link {Link}", link);
        if (!link.IsForThisGame)
        {
            await _dialogs.ShowInfoAsync(
                "Link for another game",
                $"This Nexus Mods link is for '{link.GameDomain}', not for {AppInfo.GameDisplayName}. Use the mod manager for that game instead.");
            return;
        }

        if (!await CheckPreconditionsAsync("installing mods"))
        {
            return;
        }

        if (!_session.HasApiKey)
        {
            await _dialogs.ShowErrorAsync(
                "Nexus Mods API key needed",
                "The download link needs your Nexus Mods account. Add your API key on the Settings page, then click \"Mod Manager Download\" on the website again.");
            return;
        }

        if (link.UserId is { } linkUser && _session.User is { } user && user.UserId != linkUser)
        {
            await _dialogs.ShowErrorAsync(
                "Link for another account",
                $"This download link was made for a different Nexus Mods account than the one connected here ({user.Name}). Log into nexusmods.com with the same account and click \"Mod Manager Download\" again.");
            return;
        }

        if (link.IsExpired)
        {
            await _dialogs.ShowErrorAsync("Link expired", "This download link has expired. Open the mod page again and click \"Mod Manager Download\" once more.");
            return;
        }

        NexusMod mod;
        NexusFile? file;
        try
        {
            (mod, file) = await _dialogs.RunWithProgressAsync(
                "Opening Nexus Mods link",
                async (progress, cancellationToken) =>
                {
                    progress.Report(new ProgressUpdate("Reading mod information from Nexus Mods..."));
                    var m = await _client.GetModAsync(link.ModId, cancellationToken: cancellationToken);
                    var files = await _client.GetFilesAsync(link.ModId, cancellationToken: cancellationToken);
                    return (m, files.Files.FirstOrDefault(f => f.FileId == link.FileId));
                },
                canCancel: false);
        }
        catch (NexusException ex)
        {
            _logger.LogWarning("nxm link could not be resolved: {Message}", ex.Message);
            await _dialogs.ShowErrorAsync("Could not open the Nexus Mods link", ex.Message, ex.ToString());
            return;
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // The file list may lag behind a brand-new upload; the download route only needs the ids.
        file ??= new NexusFile(link.FileId, mod.Name, mod.Version ?? string.Empty, NexusFileCategory.Main, true, 0, null, DateTimeOffset.UtcNow, mod.Version, null, null);
        await InstallFromNexusCoreAsync(mod, file, link, updateOf: null);
    }

    /// <summary>Resolves the download location, downloads with progress, then runs the normal install flow.</summary>
    private async Task<ModEntry?> DownloadAndInstallAsync(NexusMod mod, NexusFile file, NxmLink? link, Guid? updateOf)
    {
        if (!_session.HasApiKey)
        {
            await _dialogs.ShowErrorAsync(
                "Nexus Mods API key needed",
                "Downloads use your Nexus Mods account. Add your API key on the Settings page, then try again. Browsing works without it.");
            return null;
        }

        // Free accounts download through the website; the API only hands out links for premium members or with
        // the token the website puts into its nxm:// links.
        if (link is null && !_session.IsPremium && !mod.DirectDownloadEnabled)
        {
            return await SendToWebsiteAsync(mod, file);
        }

        IReadOnlyList<NexusDownloadLink> links;
        try
        {
            links = await _dialogs.RunWithProgressAsync(
                $"Downloading {file.Name}",
                (progress, cancellationToken) =>
                {
                    progress.Report(new ProgressUpdate("Asking Nexus Mods for the download location..."));
                    return _client.GetDownloadLinksAsync(mod.ModId, file.FileId, link?.Key, link?.Expires, cancellationToken);
                },
                canCancel: false);
        }
        catch (NexusPremiumRequiredException)
        {
            return await SendToWebsiteAsync(mod, file);
        }
        catch (NexusException ex)
        {
            _logger.LogWarning("Download links for mod {Mod} file {File} failed: {Message}", mod.ModId, file.FileId, ex.Message);
            await _dialogs.ShowErrorAsync($"Could not download {file.Name}", ex.Message, ex.ToString());
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        var url = links[0].Uri;
        var fallbackName = $"{FolderNameSanitizer.Sanitize(mod.Name)}-{mod.ModId}-{file.FileId}.zip";
        var fileName = string.IsNullOrWhiteSpace(file.FileName) ? _downloads.FileNameFor(url, fallbackName) : file.FileName;

        string archivePath;
        try
        {
            archivePath = await _dialogs.RunWithProgressAsync(
                $"Downloading {file.Name}",
                (progress, cancellationToken) =>
                {
                    var adapter = new Progress<DownloadProgress>(d => progress.Report(new ProgressUpdate(Format.Download(d.BytesReceived, d.TotalBytes, d.BytesPerSecond), d.Fraction)));
                    return _downloads.DownloadAsync(url, _paths.DownloadsDirectory, fileName, adapter, cancellationToken);
                });
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Download of {File} cancelled", fileName);
            return null;
        }
        catch (NexusException ex)
        {
            _logger.LogWarning("Download of {File} failed: {Message}", fileName, ex.Message);
            await _dialogs.ShowErrorAsync($"Could not download {file.Name}", ex.Message, ex.ToString());
            return null;
        }

        var metadata = new ModMetadata(
            ModSource.Nexus,
            mod.ModId,
            file.FileId,
            string.IsNullOrWhiteSpace(file.Version) ? mod.Version : file.Version,
            mod.Author ?? mod.Uploader,
            mod.ThumbnailUrl ?? mod.PictureUrl);

        var entry = await InstallOneAsync(InstallSource.FromArchive(archivePath), mod.Name, metadata, updateOf);
        if (entry is not null)
        {
            TryDeleteFile(archivePath);
            if (updateOf is not null)
            {
                _updates.Clear(entry.Id);
            }
        }

        return entry;
    }

    /// <summary>Free account without a website token: explain and open the file's page.</summary>
    private async Task<ModEntry?> SendToWebsiteAsync(NexusMod mod, NexusFile file)
    {
        _logger.LogInformation("Free account: sending the user to the website for mod {Mod} file {File}", mod.ModId, file.FileId);
        var open = await _dialogs.ConfirmAsync(
            "Download on Nexus Mods",
            "Free Nexus Mods accounts start downloads on the website: open the mod's Files tab, find the file and click "
            + $"\"Mod Manager Download\". The download then opens in {AppInfo.DisplayName} (make sure \"Handle nxm:// links\" is on in Settings).",
            "Open the Files tab");
        if (open)
        {
            _windows.OpenUrl(mod.FilesUrl);
        }

        return null;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Shared install flow
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>Game configured, not running, mod store ready; otherwise tells the user and returns false.</summary>
    private async Task<bool> CheckPreconditionsAsync(string activity)
    {
        if (_game.Current is null)
        {
            await _dialogs.ShowErrorAsync("Game folder not set", $"Choose your {AppInfo.GameDisplayName} folder in Settings before {activity}.");
            return false;
        }

        if (!await EnsureGameNotRunningAsync())
        {
            return false;
        }

        if (_mods.InitializationError is not null)
        {
            _logger.LogInformation("Install refused: the mod store is not ready ({Error})", _mods.InitializationError.Message);
            await _dialogs.ShowErrorAsync(
                "Mod list unavailable",
                "The mod list could not be prepared, so nothing can be installed right now. Use \"Try again\" on the Installed page first.");
            return false;
        }

        return true;
    }

    /// <summary>Inspects, lets the user choose, and installs one source. Returns null when cancelled or failed (after telling the user).</summary>
    private async Task<ModEntry?> InstallOneAsync(InstallSource source, string? preferredName, ModMetadata? metadata, Guid? updateOf)
    {
        InstallPlan plan;
        try
        {
            plan = await _dialogs.RunWithProgressAsync(
                $"Preparing {preferredName ?? source.DisplayName}",
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
            await _dialogs.ShowErrorAsync($"Could not install {preferredName ?? source.DisplayName}", ex.Message, ex.InnerException?.ToString());
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Inspection of {Source} failed", source.DisplayName);
            await _dialogs.ShowErrorAsync(
                $"Could not install {preferredName ?? source.DisplayName}",
                "The package could not be read. Make sure the file is complete and not in use, then try again.",
                ex.ToString());
            return null;
        }

        using (plan)
        {
            var label = preferredName ?? source.DisplayName;
            if (plan.IsEmpty)
            {
                await _dialogs.ShowErrorAsync($"Could not install {label}", $"No mod files (.pak with .ucas/.utoc) were found in {source.DisplayName}.");
                return null;
            }

            IReadOnlyList<ModFileSet> selected;
            string name;
            if (plan.RequiresChoice)
            {
                var choice = await _dialogs.ShowInstallPickerAsync(plan, preferredName);
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
                name = preferredName ?? plan.SuggestedName;
            }

            // The game may have started while a dialog was open.
            if (!await EnsureGameNotRunningAsync())
            {
                return null;
            }

            return await InstallWithRetriesAsync(plan, selected, name, metadata, updateOf);
        }
    }

    /// <summary>
    /// Runs the install, resolving a folder conflict through a choice dialog and an access-denied error through
    /// the one-time permission fix; other failures become error dialogs.
    /// </summary>
    private async Task<ModEntry?> InstallWithRetriesAsync(InstallPlan plan, IReadOnlyList<ModFileSet> selected, string name, ModMetadata? metadata, Guid? updateOf)
    {
        var resolution = ConflictResolution.Ask;
        var permissionFixOffered = false;
        var title = updateOf is null ? $"Could not install {name}" : $"Could not update {name}";

        while (true)
        {
            // The game may have started while a conflict or permission dialog was open.
            if (!await EnsureGameNotRunningAsync())
            {
                return null;
            }

            var request = new InstallRequest(plan, selected, name, resolution, metadata, updateOf);
            try
            {
                var entry = await _dialogs.RunWithProgressAsync(
                    updateOf is null ? $"Installing {name}" : $"Updating {name}",
                    (progress, cancellationToken) => _installer.InstallAsync(request, Adapt(progress), cancellationToken));

                if (updateOf is not null)
                {
                    _logger.LogInformation("Updated '{Mod}' ({Id}) from {Source} to version {Version}", entry.DisplayName, entry.Id, plan.Source.DisplayName, entry.Version ?? "?");
                    Updated?.Invoke(this, entry);
                    return entry;
                }

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

    private void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                _logger.LogDebug("Deleted downloaded archive {Path}", path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Downloaded archive {Path} could not be deleted", path);
        }
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
