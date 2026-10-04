using System.Collections.Immutable;
using DungeonsModLoader.Core.Game;
using Microsoft.Extensions.Logging;

namespace DungeonsModLoader.Core.Mods;

/// <summary>
/// <inheritdoc cref="IModService"/>
/// <para>
/// The manifest is the source of truth for <em>which</em> mods exist; the disk decides their state: a folder
/// inside <c>~mods</c> is Enabled, one inside the app's disabled folder is Disabled, and an entry with neither
/// folder is Missing. Every mutation runs under one gate so two operations can never race on the same folder.
/// Every path this service touches is derived from a validated plain folder name under one of the two mod
/// folders, so game files are never moved or deleted by mistake.
/// </para>
/// </summary>
public sealed class ModService : IModService, IDisposable
{
    private const int E_ACCESSDENIED = unchecked((int)0x80070005);

    private readonly IGameContext _gameContext;
    private readonly IManifestStore _manifestStore;
    private readonly ILogger<ModService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private Manifest _manifest = new();
    private ImmutableArray<ModInfo> _mods = ImmutableArray<ModInfo>.Empty;
    private ImmutableArray<string> _unmanaged = ImmutableArray<string>.Empty;
    private bool _disposed;

    public ModService(IGameContext gameContext, IManifestStore manifestStore, ILogger<ModService> logger)
    {
        _gameContext = gameContext;
        _manifestStore = manifestStore;
        _logger = logger;
        _gameContext.Changed += OnGameContextChanged;
    }

    public IReadOnlyList<ModInfo> Mods => _mods;

    public IReadOnlyList<string> UnmanagedFolders => _unmanaged;

    public bool IsInitialized { get; private set; }

    public Exception? InitializationError { get; private set; }

    public event EventHandler? Changed;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        bool raise;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var installation = _gameContext.Current;
            if (installation is null)
            {
                raise = ClearStateCore() | InitializationError is not null;
                InitializationError = null;
                _logger.LogInformation("No game installation configured; mod store is idle");
            }
            else
            {
                _logger.LogInformation("Initializing mod store for {Root}", installation.Root);
                try
                {
                    _manifest = await _manifestStore.LoadAsync(cancellationToken).ConfigureAwait(false);
                    await Task.Run(() => ReconcileCore(installation), cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Never keep a previous installation's mods on screen after a failed switch: clear everything,
                    // remember why, tell listeners, then let the caller handle the error.
                    ClearStateCore();
                    InitializationError = ex;
                    _logger.LogError(ex, "The mod store could not be initialized for {Root}", installation.Root);
                    throw;
                }

                IsInitialized = true;
                InitializationError = null;
                raise = true;
                _logger.LogInformation(
                    "Mod store ready: {Managed} managed mods ({Missing} missing), {Unmanaged} unmanaged folders",
                    _mods.Length,
                    _mods.Count(m => m.IsMissing),
                    _unmanaged.Length);
            }
        }
        catch
        {
            _gate.Release();
            RaiseChanged();
            throw;
        }

        _gate.Release();
        if (raise)
        {
            RaiseChanged();
        }
    }

    public async Task<ReconcileResult> ReconcileAsync(CancellationToken cancellationToken = default)
    {
        bool raise;
        ReconcileResult result;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var installation = _gameContext.Current;
            if (installation is null)
            {
                raise = ClearStateCore();
                result = new ReconcileResult(Array.Empty<string>(), Array.Empty<ModEntry>());
            }
            else
            {
                var before = (_mods, _unmanaged);
                await Task.Run(() => ReconcileCore(installation), cancellationToken).ConfigureAwait(false);
                raise = HasSnapshotChanged(before.Item1, before.Item2, _mods, _unmanaged);
                result = new ReconcileResult(_unmanaged, _mods.Where(m => m.IsMissing).Select(m => m.Entry).ToList());
            }
        }
        finally
        {
            _gate.Release();
        }

        if (raise)
        {
            RaiseChanged();
        }

        return result;
    }

    public async Task SetEnabledAsync(Guid modId, bool enabled, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var installation = RequireInstallation();
            var entry = RequireEntry(modId);
            await Task.Run(() => MoveModCore(installation, entry, enabled), cancellationToken).ConfigureAwait(false);
            RefreshSnapshotCore(installation);
        }
        finally
        {
            _gate.Release();
        }

        RaiseChanged();
    }

    public async Task<IReadOnlyList<ModOperationFailure>> SetAllEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        var failures = new List<ModOperationFailure>();
        var attempted = 0;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var installation = RequireInstallation();
            var targets = _manifest.Mods
                .Where(entry =>
                {
                    var state = ComputeState(installation, entry, warnOnDuplicate: false);
                    return state != ModState.Missing && (state == ModState.Enabled) != enabled;
                })
                .ToList();

            foreach (var entry in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                attempted++;
                try
                {
                    await Task.Run(() => MoveModCore(installation, entry, enabled), cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Could not {Action} '{Mod}' ({Folder})", enabled ? "enable" : "disable", entry.DisplayName, entry.FolderName);
                    failures.Add(new ModOperationFailure(entry, ex));
                }
            }

            RefreshSnapshotCore(installation);
            _logger.LogInformation("{Action} all: {Succeeded} succeeded, {Failed} failed", enabled ? "Enable" : "Disable", attempted - failures.Count, failures.Count);
        }
        finally
        {
            _gate.Release();
        }

        if (attempted > 0)
        {
            RaiseChanged();
        }

        return failures;
    }

    public async Task<ModEntry> ImportUnmanagedAsync(string folderName, string? displayName = null, CancellationToken cancellationToken = default)
    {
        if (!IsPlainFolderName(folderName))
        {
            throw new ArgumentException($"'{folderName}' is not a valid folder name.", nameof(folderName));
        }

        // Phase 1 (under the gate): validate. Phase 2 (outside): hash, which can take a while for big mods and must
        // not block toggles, reconcile or launch. Phase 3 (under the gate): re-validate and record.
        string folderPath;
        string actualName;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var installation = RequireInstallation();
            folderPath = Path.Combine(installation.ModsDirectory, folderName);
            EnsureUnmanagedFolder(installation, folderName, folderPath);

            // Record the folder name exactly as the file system spells it.
            actualName = GetActualFolderName(installation.ModsDirectory, folderName);
        }
        finally
        {
            _gate.Release();
        }

        List<ModFileRecord> files;
        try
        {
            files = await Task.Run(() => FileHasher.HashDirectoryAsync(folderPath, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new ModAccessDeniedException(folderPath, ex);
        }

        ModEntry entry;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var installation = RequireInstallation();
            EnsureUnmanagedFolder(installation, folderName, folderPath);

            var now = DateTimeOffset.UtcNow;
            entry = new ModEntry
            {
                FolderName = actualName,
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? actualName : displayName.Trim(),
                Source = ModSource.Local,
                InstalledAt = now,
                UpdatedAt = now,
                Files = files,
            };

            _manifest.Mods.Add(entry);
            await SaveManifestOrRevertAsync(() => _manifest.Mods.Remove(entry), installation, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Imported unmanaged folder '{Folder}' as '{Mod}' ({Files} files)", actualName, entry.DisplayName, files.Count);
        }
        finally
        {
            _gate.Release();
        }

        RaiseChanged();
        return entry;
    }

    /// <summary>Throws when <paramref name="folderName"/> is not an existing, not-yet-managed folder inside <c>~mods</c>.</summary>
    private void EnsureUnmanagedFolder(GameInstallation installation, string folderName, string folderPath)
    {
        if (!Directory.Exists(folderPath))
        {
            throw new ModNotFoundException($"There is no folder named '{folderName}' in {installation.ModsDirectory}.");
        }

        var existing = _manifest.Mods.FirstOrDefault(m => string.Equals(m.FolderName, folderName, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            throw new InvalidOperationException($"The folder '{folderName}' is already managed as '{existing.DisplayName}'.");
        }
    }

    /// <summary>
    /// Saves the manifest and refreshes the snapshot. When the save fails the in-memory change is undone first
    /// (via <paramref name="revert"/>) so the UI never shows a state that is not on disk. Runs under the gate.
    /// </summary>
    private async Task SaveManifestOrRevertAsync(Action revert, GameInstallation installation, CancellationToken cancellationToken)
    {
        try
        {
            await _manifestStore.SaveAsync(_manifest, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "The manifest could not be saved; the change was undone");
            revert();
            throw new ModOperationException(
                "The change could not be saved to the mod list (manifest.json). Make sure the app data folder is writable and try again.",
                ex);
        }
        finally
        {
            TryRefreshSnapshot(installation);
        }
    }

    /// <summary>Refreshes the snapshot without letting a disk problem mask the original outcome of an operation.</summary>
    private void TryRefreshSnapshot(GameInstallation installation)
    {
        try
        {
            RefreshSnapshotCore(installation);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ModAccessDeniedException)
        {
            _logger.LogWarning(ex, "The mod snapshot could not be refreshed");
        }
    }

    public async Task AddInstalledAsync(ModEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!IsPlainFolderName(entry.FolderName))
        {
            throw new ArgumentException($"'{entry.FolderName}' is not a valid folder name.", nameof(entry));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var installation = RequireInstallation();
            if (!Directory.Exists(Path.Combine(installation.ModsDirectory, entry.FolderName)))
            {
                throw new ModNotFoundException($"There is no folder named '{entry.FolderName}' in {installation.ModsDirectory}.");
            }

            var clash = _manifest.Mods.FirstOrDefault(m =>
                m.Id == entry.Id || string.Equals(m.FolderName, entry.FolderName, StringComparison.OrdinalIgnoreCase));
            if (clash is not null)
            {
                throw new InvalidOperationException($"The folder '{entry.FolderName}' is already managed as '{clash.DisplayName}'.");
            }

            if (string.IsNullOrWhiteSpace(entry.DisplayName))
            {
                entry.DisplayName = entry.FolderName;
            }

            _manifest.Mods.Add(entry);
            await SaveManifestOrRevertAsync(() => _manifest.Mods.Remove(entry), installation, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Recorded installed mod '{Mod}' ({Folder}, {Files} files)", entry.DisplayName, entry.FolderName, entry.Files.Count);
        }
        finally
        {
            _gate.Release();
        }

        RaiseChanged();
    }

    public async Task RenameAsync(Guid modId, string displayName, CancellationToken cancellationToken = default)
    {
        var trimmed = displayName?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("The mod name cannot be empty.", nameof(displayName));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var installation = RequireInstallation();
            var entry = RequireEntry(modId);
            if (string.Equals(entry.DisplayName, trimmed, StringComparison.Ordinal))
            {
                return;
            }

            _logger.LogInformation("Renaming '{Old}' to '{New}'", entry.DisplayName, trimmed);
            var previousName = entry.DisplayName;
            var previousUpdatedAt = entry.UpdatedAt;
            entry.DisplayName = trimmed;
            entry.UpdatedAt = DateTimeOffset.UtcNow;
            await SaveManifestOrRevertAsync(
                () =>
                {
                    entry.DisplayName = previousName;
                    entry.UpdatedAt = previousUpdatedAt;
                },
                installation,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        RaiseChanged();
    }

    public async Task UninstallAsync(Guid modId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var installation = RequireInstallation();
            var entry = RequireEntry(modId);

            // An entry whose folder name is not a plain name is always reported Missing (ComputeState), so it is
            // removed from the manifest without touching the disk: nothing outside the mod folders is ever deleted.
            var state = ComputeState(installation, entry, warnOnDuplicate: false);
            if (state != ModState.Missing)
            {
                var folderPath = state == ModState.Enabled
                    ? Path.Combine(installation.ModsDirectory, entry.FolderName)
                    : Path.Combine(installation.DisabledModsDirectory, entry.FolderName);
                await Task.Run(() => DeleteModFolderCore(installation, folderPath), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                _logger.LogInformation("Uninstalling missing mod '{Mod}' ({Folder}): removing the manifest entry only", entry.DisplayName, entry.FolderName);
            }

            var index = _manifest.Mods.IndexOf(entry);
            _manifest.Mods.Remove(entry);
            await SaveManifestOrRevertAsync(
                () => _manifest.Mods.Insert(Math.Clamp(index, 0, _manifest.Mods.Count), entry),
                installation,
                cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Uninstalled '{Mod}' ({Folder})", entry.DisplayName, entry.FolderName);
        }
        finally
        {
            _gate.Release();
        }

        RaiseChanged();
    }

    public ModInfo? Find(Guid modId)
    {
        foreach (var mod in _mods)
        {
            if (mod.Entry.Id == modId)
            {
                return mod;
            }
        }

        return null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gameContext.Changed -= OnGameContextChanged;
        _gate.Dispose();
    }

    /// <summary>
    /// True when <paramref name="name"/> is a single path segment that can only ever resolve to a direct child of
    /// the folder it is combined with (no separators, no drive, not "." or "..", no trailing dot/space).
    /// </summary>
    public static bool IsPlainFolderName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or "..")
        {
            return false;
        }

        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/') || name.Contains('\\'))
        {
            return false;
        }

        if (name[^1] is '.' or ' ' || name[0] == ' ')
        {
            return false;
        }

        return string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal);
    }

    private void OnGameContextChanged(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        _ = ReinitializeAsync();
    }

    private async Task ReinitializeAsync()
    {
        try
        {
            _logger.LogInformation("Game installation changed; re-initializing the mod store");
            await InitializeAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // The service was disposed while the game context changed (app shutdown); nothing to do.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The mod store could not be re-initialized after the game installation changed");
        }
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A Changed handler of the mod store threw");
        }
    }

    private GameInstallation RequireInstallation()
    {
        return _gameContext.Current ?? throw new InvalidOperationException("No game installation is configured.");
    }

    private ModEntry RequireEntry(Guid modId)
    {
        return _manifest.Mods.FirstOrDefault(m => m.Id == modId)
            ?? throw new ModNotFoundException($"No installed mod has the id {modId}.");
    }

    /// <summary>Clears every snapshot; returns true when there was something to clear.</summary>
    private bool ClearStateCore()
    {
        var hadState = IsInitialized || _mods.Length > 0 || _unmanaged.Length > 0;
        _manifest = new Manifest();
        _mods = ImmutableArray<ModInfo>.Empty;
        _unmanaged = ImmutableArray<string>.Empty;
        IsInitialized = false;
        return hadState;
    }

    /// <summary>Ensures both mod folders exist, then rebuilds the snapshots. Runs under the gate.</summary>
    private void ReconcileCore(GameInstallation installation)
    {
        EnsureDirectory(installation.ModsDirectory);
        EnsureDirectory(installation.DisabledModsDirectory);
        CleanupInternalFolders(installation);
        RefreshSnapshotCore(installation);
    }

    /// <summary>Prefix of the app's own temporary folders (install staging); never shown as mods, deleted when stale.</summary>
    public const string InternalFolderPrefix = ".dml-";

    private static bool IsInternalFolder(string name) => name.StartsWith(InternalFolderPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Removes build folders a crashed install left behind in either mod folder.</summary>
    private void CleanupInternalFolders(GameInstallation installation)
    {
        foreach (var parent in new[] { installation.ModsDirectory, installation.DisabledModsDirectory })
        {
            try
            {
                foreach (var directory in Directory.EnumerateDirectories(parent, InternalFolderPrefix + "*"))
                {
                    _logger.LogInformation("Removing leftover install folder {Path}", directory);
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Leftover install folders under {Parent} could not be cleaned", parent);
            }
        }
    }

    /// <summary>Recomputes <see cref="Mods"/> and <see cref="UnmanagedFolders"/> from the manifest and the disk. Runs under the gate.</summary>
    private void RefreshSnapshotCore(GameInstallation installation)
    {
        var mods = _manifest.Mods
            .Select(entry => CreateInfo(installation, entry))
            .OrderBy(m => m.Entry.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(m => m.Entry.FolderName, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();

        var managed = new HashSet<string>(_manifest.Mods.Select(m => m.FolderName), StringComparer.OrdinalIgnoreCase);
        var unmanaged = ImmutableArray<string>.Empty;
        if (Directory.Exists(installation.ModsDirectory))
        {
            try
            {
                unmanaged = Directory.EnumerateDirectories(installation.ModsDirectory)
                    .Select(Path.GetFileName)
                    .Where(name => !string.IsNullOrEmpty(name) && !managed.Contains(name) && !IsInternalFolder(name))
                    .Select(name => name!)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToImmutableArray();
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new ModAccessDeniedException(installation.ModsDirectory, ex);
            }
        }

        _mods = mods;
        _unmanaged = unmanaged;
    }

    private ModInfo CreateInfo(GameInstallation installation, ModEntry entry)
    {
        var state = ComputeState(installation, entry, warnOnDuplicate: true);
        var folderPath = state switch
        {
            ModState.Enabled => Path.Combine(installation.ModsDirectory, entry.FolderName),
            ModState.Disabled => Path.Combine(installation.DisabledModsDirectory, entry.FolderName),
            _ => null,
        };

        return new ModInfo { Entry = entry, State = state, FolderPath = folderPath };
    }

    private ModState ComputeState(GameInstallation installation, ModEntry entry, bool warnOnDuplicate)
    {
        if (!IsPlainFolderName(entry.FolderName))
        {
            if (warnOnDuplicate)
            {
                _logger.LogWarning("Manifest entry '{Mod}' has an invalid folder name '{Folder}'; treating it as missing", entry.DisplayName, entry.FolderName);
            }

            return ModState.Missing;
        }

        var enabledExists = Directory.Exists(Path.Combine(installation.ModsDirectory, entry.FolderName));
        var disabledExists = Directory.Exists(Path.Combine(installation.DisabledModsDirectory, entry.FolderName));
        if (enabledExists && disabledExists && warnOnDuplicate)
        {
            _logger.LogWarning(
                "'{Mod}' exists in both {Mods} and {Disabled}; the enabled copy wins and the disabled copy is left untouched",
                entry.FolderName,
                installation.ModsDirectory,
                installation.DisabledModsDirectory);
        }

        if (enabledExists)
        {
            return ModState.Enabled;
        }

        return disabledExists ? ModState.Disabled : ModState.Missing;
    }

    /// <summary>Moves one mod folder between the two locations. Runs under the gate on a worker thread.</summary>
    private void MoveModCore(GameInstallation installation, ModEntry entry, bool enabled)
    {
        if (!IsPlainFolderName(entry.FolderName))
        {
            throw new ModNotFoundException($"The folder name '{entry.FolderName}' of '{entry.DisplayName}' is invalid.");
        }

        var state = ComputeState(installation, entry, warnOnDuplicate: false);
        if (state == ModState.Missing)
        {
            throw new ModNotFoundException(
                $"The folder '{entry.FolderName}' for '{entry.DisplayName}' was not found in {installation.ModsDirectory} or {installation.DisabledModsDirectory}.");
        }

        if ((state == ModState.Enabled) == enabled)
        {
            return;
        }

        var sourceParent = enabled ? installation.DisabledModsDirectory : installation.ModsDirectory;
        var targetParent = enabled ? installation.ModsDirectory : installation.DisabledModsDirectory;
        var source = Path.Combine(sourceParent, entry.FolderName);
        var target = Path.Combine(targetParent, entry.FolderName);
        EnsureDirectlyUnder(source, sourceParent);
        EnsureDirectlyUnder(target, targetParent);

        if (Directory.Exists(target))
        {
            throw new ModOperationException($"A folder named '{entry.FolderName}' already exists in {targetParent}. Remove or rename it, then try again.");
        }

        EnsureDirectory(targetParent);
        _logger.LogInformation("{Action} '{Mod}': moving {Source} -> {Target}", enabled ? "Enabling" : "Disabling", entry.DisplayName, source, target);
        try
        {
            Directory.Move(source, target);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new ModAccessDeniedException(source, ex);
        }
        catch (IOException ex) when (ex.HResult == E_ACCESSDENIED)
        {
            // Windows reports both "no modify rights" and "a file inside is open" as access denied.
            // Probe the parents: if we can write there, the folder is merely in use.
            if (!CanWriteTo(sourceParent) || !CanWriteTo(targetParent))
            {
                throw new ModAccessDeniedException(source, ex);
            }

            throw new ModOperationException(
                $"The folder '{entry.FolderName}' is in use and could not be moved. Close the game and any program using its files, then try again.",
                ex);
        }

        if (Directory.Exists(source) || !Directory.Exists(target))
        {
            throw new ModOperationException($"The move of '{entry.FolderName}' could not be verified: the folder is not where it should be.");
        }
    }

    /// <summary>Deletes a mod folder, refusing anything that is not directly inside one of the two mod folders.</summary>
    private void DeleteModFolderCore(GameInstallation installation, string folderPath)
    {
        var full = Path.GetFullPath(folderPath);
        if (!IsDirectlyUnder(full, installation.ModsDirectory) && !IsDirectlyUnder(full, installation.DisabledModsDirectory))
        {
            throw new InvalidOperationException($"Refusing to delete '{full}': it is not inside the mod folders.");
        }

        if (!Directory.Exists(full))
        {
            return;
        }

        _logger.LogInformation("Deleting mod folder {Path}", full);
        try
        {
            try
            {
                Directory.Delete(full, recursive: true);
            }
            catch (UnauthorizedAccessException)
            {
                // Files extracted from archives are often read-only; clear that and try once more.
                ClearReadOnlyAttributes(full);
                Directory.Delete(full, recursive: true);
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new ModAccessDeniedException(full, ex);
        }
        catch (IOException ex) when (ex.HResult == E_ACCESSDENIED && !CanWriteTo(Path.GetDirectoryName(full)!))
        {
            throw new ModAccessDeniedException(full, ex);
        }

        if (Directory.Exists(full))
        {
            throw new ModOperationException($"The folder '{full}' could not be deleted completely. Close any program using its files and try again.");
        }
    }

    private static void ClearReadOnlyAttributes(string directory)
    {
        var info = new DirectoryInfo(directory);
        foreach (var entry in info.EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
        {
            if ((entry.Attributes & FileAttributes.ReadOnly) != 0)
            {
                entry.Attributes &= ~FileAttributes.ReadOnly;
            }
        }

        if ((info.Attributes & FileAttributes.ReadOnly) != 0)
        {
            info.Attributes &= ~FileAttributes.ReadOnly;
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

    private static void EnsureDirectlyUnder(string path, string parent)
    {
        if (!IsDirectlyUnder(Path.GetFullPath(path), parent))
        {
            throw new InvalidOperationException($"Refusing to touch '{path}': it is not directly inside {parent}.");
        }
    }

    /// <summary>True when <paramref name="fullPath"/> is an immediate child of <paramref name="parent"/>.</summary>
    public static bool IsDirectlyUnder(string fullPath, string parent)
    {
        var normalizedParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        var normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(fullPath));
        var actualParent = Path.GetDirectoryName(normalizedPath);
        if (actualParent is null)
        {
            return false;
        }

        var name = Path.GetFileName(normalizedPath);
        return IsPlainFolderName(name)
            && string.Equals(Path.TrimEndingDirectorySeparator(actualParent), normalizedParent, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Checks modify rights on a folder by creating and immediately removing a probe file.</summary>
    private static bool CanWriteTo(string directory)
    {
        try
        {
            var probe = Path.Combine(directory, ".dml-probe-" + Guid.NewGuid().ToString("N")[..8]);
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
            }

            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            // Not a permission problem (for example the folder vanished); do not blame permissions.
            return true;
        }
    }

    private static string GetActualFolderName(string parent, string folderName)
    {
        try
        {
            var match = Directory.EnumerateDirectories(parent, folderName, SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .FirstOrDefault(name => string.Equals(name, folderName, StringComparison.OrdinalIgnoreCase));
            return match ?? folderName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return folderName;
        }
    }

    private static bool HasSnapshotChanged(ImmutableArray<ModInfo> oldMods, ImmutableArray<string> oldUnmanaged, ImmutableArray<ModInfo> newMods, ImmutableArray<string> newUnmanaged)
    {
        if (!oldUnmanaged.SequenceEqual(newUnmanaged, StringComparer.Ordinal))
        {
            return true;
        }

        if (oldMods.Length != newMods.Length)
        {
            return true;
        }

        for (var i = 0; i < oldMods.Length; i++)
        {
            var a = oldMods[i];
            var b = newMods[i];
            if (a.Entry.Id != b.Entry.Id || a.State != b.State || !string.Equals(a.FolderPath, b.FolderPath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
