using DungeonsModLoader.Core.Game;
using DungeonsModLoader.Core.Mods;
using Microsoft.Extensions.Logging;

namespace DungeonsModLoader.Core.Install;

/// <inheritdoc cref="IModInstaller"/>
public sealed class ModInstaller : IModInstaller
{
    private const string StagingPrefix = "install-";
    private const string BuildPrefix = ".dml-install-";
    private static readonly TimeSpan StaleStagingAge = TimeSpan.FromDays(1);

    private readonly IGameContext _game;
    private readonly IModService _mods;
    private readonly AppPaths _paths;
    private readonly ILogger<ModInstaller> _logger;

    public ModInstaller(IGameContext game, IModService mods, AppPaths paths, ILogger<ModInstaller> logger)
    {
        _game = game;
        _mods = mods;
        _paths = paths;
        _logger = logger;
    }

    public async Task<InstallPlan> InspectAsync(InstallSource source, IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        CleanupStaleStaging();

        string contentRoot;
        Action? cleanup = null;

        if (source.Kind == InstallSourceKind.Folder)
        {
            var folder = Path.GetFullPath(source.Paths[0]);
            if (!Directory.Exists(folder))
            {
                throw new InstallPackageException($"The folder '{folder}' does not exist.");
            }

            contentRoot = folder;
        }
        else
        {
            var staging = Path.Combine(_paths.TempDirectory, StagingPrefix + Guid.NewGuid().ToString("N")[..12]);
            Directory.CreateDirectory(staging);
            cleanup = () => TryDelete(staging);
            try
            {
                contentRoot = Path.Combine(staging, "content");
                Directory.CreateDirectory(contentRoot);
                if (source.Kind == InstallSourceKind.Archive)
                {
                    var archive = source.Paths[0];
                    if (!File.Exists(archive))
                    {
                        throw new InstallPackageException($"The file '{archive}' does not exist.");
                    }

                    progress?.Report(new InstallProgress($"Extracting {Path.GetFileName(archive)}..."));
                    await ArchiveExtractor.ExtractAsync(archive, contentRoot, progress, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    progress?.Report(new InstallProgress("Collecting mod files..."));
                    await Task.Run(() => CopyLooseFiles(source.Paths, contentRoot, cancellationToken), cancellationToken).ConfigureAwait(false);
                }
            }
            catch
            {
                cleanup();
                throw;
            }
        }

        try
        {
            progress?.Report(new InstallProgress("Looking for mod files..."));
            var contents = await Task.Run(() => ModPackageInspector.Inspect(contentRoot), cancellationToken).ConfigureAwait(false);
            if (!contents.Candidates.Any(c => c.FileSets.Any(s => s.IsValid)))
            {
                var detail = contents.Candidates.Count > 0
                    ? " The .pak files found are missing their .ucas/.utoc companions."
                    : string.Empty;
                throw new InstallPackageException($"No mod files (.pak with .ucas/.utoc) were found in {source.DisplayName}.{detail}");
            }

            var (name, version) = SuggestName(source, contents);
            var warnings = new List<string>(contents.Warnings);
            _logger.LogInformation(
                "Inspected {Source}: {Sets} file set(s) in {Folders} folder(s), {Extras} data file(s), {Docs} document(s); suggested name '{Name}'",
                source.DisplayName,
                contents.Candidates.Sum(c => c.FileSets.Count),
                contents.Candidates.Count,
                contents.ExtraFiles.Count,
                contents.DocumentationFiles.Count,
                name);

            return new InstallPlan(source, contents.Root, contents.Candidates, contents.ExtraFiles, contents.DocumentationFiles, name, version, warnings, cleanup);
        }
        catch
        {
            cleanup?.Invoke();
            throw;
        }
    }

    public async Task<ModEntry> InstallAsync(InstallRequest request, IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var plan = request.Plan;
        var installation = _game.Current ?? throw new InvalidOperationException("No game installation is configured.");

        var selected = request.SelectedFileSets.Where(set => set.IsValid).Distinct().ToList();
        if (selected.Count == 0)
        {
            throw new InstallPackageException("Nothing to install: select at least one complete mod file set.");
        }

        var displayName = string.IsNullOrWhiteSpace(request.DisplayName) ? plan.SuggestedName : request.DisplayName.Trim();
        var folderName = FolderNameSanitizer.Sanitize(displayName);

        // ---- conflicts -------------------------------------------------------------------------------------------
        var replacedDisplayName = (string?)null;
        var replacedWasDisabled = false;
        var existing = _mods.Mods.FirstOrDefault(m => string.Equals(m.Entry.FolderName, folderName, StringComparison.OrdinalIgnoreCase));
        var enabledPath = Path.Combine(installation.ModsDirectory, folderName);
        var disabledPath = Path.Combine(installation.DisabledModsDirectory, folderName);
        var folderExists = Directory.Exists(enabledPath) || Directory.Exists(disabledPath);

        if (existing is not null || folderExists)
        {
            switch (request.Conflict)
            {
                case ConflictResolution.Ask:
                    throw new InstallConflictException(
                        existing is not null ? InstallConflictKind.ManagedMod : InstallConflictKind.UnmanagedFolder,
                        folderName,
                        existing?.Entry);

                case ConflictResolution.KeepBoth:
                    folderName = FolderNameSanitizer.MakeUnique(folderName, candidate =>
                        _mods.Mods.Any(m => string.Equals(m.Entry.FolderName, candidate, StringComparison.OrdinalIgnoreCase))
                        || Directory.Exists(Path.Combine(installation.ModsDirectory, candidate))
                        || Directory.Exists(Path.Combine(installation.DisabledModsDirectory, candidate)));
                    enabledPath = Path.Combine(installation.ModsDirectory, folderName);
                    break;

                case ConflictResolution.Replace:
                    if (existing is not null)
                    {
                        replacedDisplayName = existing.Entry.DisplayName;
                        replacedWasDisabled = existing.State == ModState.Disabled;
                        progress?.Report(new InstallProgress($"Removing the previous version of {existing.Entry.DisplayName}..."));
                        await _mods.UninstallAsync(existing.Entry.Id, cancellationToken).ConfigureAwait(false);
                    }

                    await Task.Run(() =>
                    {
                        DeleteModFolder(installation, Path.Combine(installation.ModsDirectory, folderName));
                        DeleteModFolder(installation, Path.Combine(installation.DisabledModsDirectory, folderName));
                    }, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }

        // ---- build the folder next to its final location (same volume), then move it into ~mods -----------------
        var copyPlan = BuildCopyPlan(plan, selected);
        EnsureDirectory(installation.DisabledModsDirectory);
        EnsureDirectory(installation.ModsDirectory);
        var buildDir = Path.Combine(installation.DisabledModsDirectory, BuildPrefix + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            await Task.Run(() => CopyFiles(plan.StagingRoot, buildDir, copyPlan, progress, cancellationToken), cancellationToken).ConfigureAwait(false);

            progress?.Report(new InstallProgress($"Installing {displayName}..."));
            try
            {
                Directory.Move(buildDir, enabledPath);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new ModAccessDeniedException(enabledPath, ex);
            }
            catch (IOException ex) when (ex.HResult == unchecked((int)0x80070005))
            {
                throw new ModAccessDeniedException(enabledPath, ex);
            }

            if (!Directory.Exists(enabledPath))
            {
                throw new ModOperationException($"The mod folder '{folderName}' could not be created in {installation.ModsDirectory}.");
            }
        }
        catch
        {
            TryDelete(buildDir);
            throw;
        }

        // ---- record ----------------------------------------------------------------------------------------------
        progress?.Report(new InstallProgress("Recording the mod..."));
        var now = DateTimeOffset.UtcNow;
        var entry = new ModEntry
        {
            FolderName = folderName,
            DisplayName = replacedDisplayName ?? displayName,
            Source = ModSource.Local,
            Version = plan.SuggestedVersion,
            InstalledAt = now,
            UpdatedAt = now,
            Files = await FileHasher.HashDirectoryAsync(enabledPath, cancellationToken).ConfigureAwait(false),
        };

        try
        {
            await _mods.AddInstalledAsync(entry, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Never leave an unrecorded folder behind as if it were the user's own.
            TryDelete(enabledPath);
            throw;
        }

        if (replacedWasDisabled)
        {
            try
            {
                await _mods.SetEnabledAsync(entry.Id, false, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "The replaced mod '{Mod}' was disabled before; the new version is enabled", entry.DisplayName);
            }
        }

        _logger.LogInformation(
            "Installed '{Mod}' into {Folder}: {Files} files ({Mode})",
            entry.DisplayName,
            enabledPath,
            entry.Files.Count,
            plan.PreserveStructure ? "structure preserved" : "flattened");
        return entry;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Copy planning
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>Source (package-relative) to destination (mod-folder-relative) paths for the files to copy.</summary>
    internal static IReadOnlyList<(string Source, string Destination)> BuildCopyPlan(InstallPlan plan, IReadOnlyList<ModFileSet> selected)
    {
        var copies = new List<(string Source, string Destination)>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string source, string destination)
        {
            if (taken.Add(destination))
            {
                copies.Add((source, destination));
            }
        }

        if (!plan.PreserveStructure)
        {
            // Only pak sets (and maybe readmes): everything goes directly into the mod folder.
            foreach (var file in selected.SelectMany(set => set.Files))
            {
                Add(file, Path.GetFileName(file));
            }

            foreach (var doc in plan.DocumentationFiles)
            {
                Add(doc, Path.GetFileName(doc));
            }

            return copies;
        }

        // Data-driven package: keep the layout below the effective root (the folder that holds the selected sets
        // together with their data files), leaving out other variants' files.
        var effectiveRoot = FindEffectiveRoot(plan, selected);
        var selectedDirectories = new HashSet<string>(selected.Select(set => set.RelativeDirectory), StringComparer.OrdinalIgnoreCase);
        var excludedDirectories = plan.Candidates
            .Where(candidate => candidate.RelativeDirectory.Length > 0
                                && !selectedDirectories.Contains(candidate.RelativeDirectory)
                                && !selectedDirectories.Any(dir => IsUnder(dir, candidate.RelativeDirectory)))
            .Select(candidate => candidate.RelativeDirectory)
            .ToList();

        bool Included(string relative) =>
            IsUnder(relative, effectiveRoot) && !excludedDirectories.Any(excluded => IsUnder(relative, excluded));

        string Rebase(string relative) =>
            effectiveRoot.Length == 0 ? relative : relative[(effectiveRoot.Length + 1)..];

        foreach (var file in selected.SelectMany(set => set.Files))
        {
            Add(file, Included(file) ? Rebase(file) : Path.GetFileName(file));
        }

        foreach (var extra in plan.ExtraFiles.Where(Included))
        {
            Add(extra, Rebase(extra));
        }

        foreach (var doc in plan.DocumentationFiles.Where(Included))
        {
            Add(doc, Rebase(doc));
        }

        return copies.Select(c => (c.Source, c.Destination.Replace('/', Path.DirectorySeparatorChar))).ToList();
    }

    /// <summary>
    /// Starts at the folder shared by the selected sets and climbs towards the package root until the folder's
    /// subtree contains data files; that folder becomes the mod folder's root.
    /// </summary>
    internal static string FindEffectiveRoot(InstallPlan plan, IReadOnlyList<ModFileSet> selected)
    {
        var current = CommonDirectory(selected.Select(set => set.RelativeDirectory));
        while (true)
        {
            if (plan.ExtraFiles.Any(extra => IsUnder(extra, current)))
            {
                return current;
            }

            if (current.Length == 0)
            {
                return current;
            }

            current = ModPackageInspector.GetDirectory(current);
        }
    }

    private static string CommonDirectory(IEnumerable<string> directories)
    {
        string? common = null;
        foreach (var directory in directories)
        {
            if (common is null)
            {
                common = directory;
                continue;
            }

            while (common.Length > 0 && !IsUnder(directory, common))
            {
                common = ModPackageInspector.GetDirectory(common);
            }
        }

        return common ?? string.Empty;
    }

    /// <summary>True when <paramref name="relative"/> is <paramref name="directory"/> itself or lies beneath it ('' = root).</summary>
    private static bool IsUnder(string relative, string directory) =>
        directory.Length == 0
        || string.Equals(relative, directory, StringComparison.OrdinalIgnoreCase)
        || relative.StartsWith(directory + "/", StringComparison.OrdinalIgnoreCase);

    // ---------------------------------------------------------------------------------------------------------------
    // File work
    // ---------------------------------------------------------------------------------------------------------------

    private static void CopyLooseFiles(IReadOnlyList<string> files, string destination, CancellationToken cancellationToken)
    {
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(file))
            {
                throw new InstallPackageException($"The file '{file}' does not exist.");
            }

            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }
    }

    private static void CopyFiles(string root, string buildDir, IReadOnlyList<(string Source, string Destination)> copies, IProgress<InstallProgress>? progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(buildDir);
        var buildRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(buildDir)) + Path.DirectorySeparatorChar;
        var done = 0;
        foreach (var (source, destination) in copies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var from = Path.Combine(root, source.Replace('/', Path.DirectorySeparatorChar));
            var to = Path.GetFullPath(Path.Combine(buildDir, destination));
            if (!to.StartsWith(buildRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InstallPackageException($"Refusing to write outside the mod folder: '{destination}'.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(from, to, overwrite: true);
            File.SetAttributes(to, File.GetAttributes(to) & ~FileAttributes.ReadOnly);
            done++;
            progress?.Report(new InstallProgress($"Copying files ({done}/{copies.Count})", copies.Count == 0 ? null : done / (double)copies.Count));
        }
    }

    private static void DeleteModFolder(GameInstallation installation, string path)
    {
        var full = Path.GetFullPath(path);
        if (!Directory.Exists(full))
        {
            return;
        }

        if (!ModService.IsDirectlyUnder(full, installation.ModsDirectory) && !ModService.IsDirectlyUnder(full, installation.DisabledModsDirectory))
        {
            throw new InvalidOperationException($"Refusing to delete '{full}': it is not inside the mod folders.");
        }

        try
        {
            Directory.Delete(full, recursive: true);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new ModAccessDeniedException(full, ex);
        }
    }

    private static void EnsureDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new ModAccessDeniedException(path, ex);
        }
    }

    private static (string Name, string? Version) SuggestName(InstallSource source, PackageContents contents)
    {
        switch (source.Kind)
        {
            case InstallSourceKind.Archive:
                return ModNameHeuristics.FromArchiveName(Path.GetFileNameWithoutExtension(source.Paths[0]));
            case InstallSourceKind.Folder:
                return ModNameHeuristics.FromArchiveName(Path.GetFileName(Path.TrimEndingDirectorySeparator(source.Paths[0])));
            default:
                var first = contents.Candidates.SelectMany(c => c.FileSets).FirstOrDefault(s => s.IsValid);
                return (first is null ? source.DisplayName : ModNameHeuristics.FromFileSet(first), null);
        }
    }

    private void CleanupStaleStaging()
    {
        try
        {
            if (!Directory.Exists(_paths.TempDirectory))
            {
                return;
            }

            var cutoff = DateTime.UtcNow - StaleStagingAge;
            foreach (var directory in Directory.EnumerateDirectories(_paths.TempDirectory, StagingPrefix + "*"))
            {
                if (Directory.GetLastWriteTimeUtc(directory) < cutoff)
                {
                    TryDelete(directory);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Stale staging cleanup skipped");
        }
    }

    private void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not delete {Directory}", directory);
        }
    }
}
