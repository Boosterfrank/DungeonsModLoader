using System.Collections.Immutable;
using DungeonsModLoader.Core.Game;
using DungeonsModLoader.Core.Install;
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
/// <para>
/// Reconciling also <em>adopts</em>: a folder that appears in <c>~mods</c> (or in the disabled folder) without a
/// manifest entry was put there by hand, so it is recorded as a local mod right away; loose pak sets lying
/// directly in <c>~mods</c> are first wrapped into a folder of their own. Nothing is ever deleted by adoption.
/// </para>
/// </summary>
public sealed class ModService : IModService, IDisposable
{
    private const int E_ACCESSDENIED = unchecked((int)0x80070005);

    private readonly IGameContext _gameContext;
    private readonly IManifestStore _manifestStore;
    private readonly ILogger<ModService> _logger;
    private readonly ModServiceOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Folder names an installer is about to record itself; adoption and the unmanaged list skip them.</summary>
    private readonly HashSet<string> _reserved = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _reservedLock = new();

    private Manifest _manifest = new();
    private ImmutableArray<ModInfo> _mods = ImmutableArray<ModInfo>.Empty;
    private ImmutableArray<string> _unmanaged = ImmutableArray<string>.Empty;
    private bool _disposed;

    public ModService(IGameContext gameContext, IManifestStore manifestStore, ILogger<ModService> logger, ModServiceOptions? options = null)
    {
        _gameContext = gameContext;
        _manifestStore = manifestStore;
        _logger = logger;
        _options = options ?? ModServiceOptions.Default;
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
                    await ReconcileCoreAsync(installation, cancellationToken).ConfigureAwait(false);
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
                result = new ReconcileResult(Array.Empty<string>(), Array.Empty<ModEntry>(), Array.Empty<ModEntry>());
            }
            else
            {
                var before = (_mods, _unmanaged);
                var adopted = await ReconcileCoreAsync(installation, cancellationToken).ConfigureAwait(false);
                raise = adopted.Count > 0 || HasSnapshotChanged(before.Item1, before.Item2, _mods, _unmanaged);
                result = new ReconcileResult(_unmanaged, _mods.Where(m => m.IsMissing).Select(m => m.Entry).ToList(), adopted);
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

    public async Task ApplyEnabledStatesAsync(IReadOnlySet<Guid> enabledModIds, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(enabledModIds);

        var raise = false;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var installation = RequireInstallation();
            var moves = new List<(ModEntry Entry, bool Enable)>();
            foreach (var entry in _manifest.Mods)
            {
                var state = ComputeState(installation, entry, warnOnDuplicate: false);
                if (state == ModState.Missing)
                {
                    continue;
                }

                var enable = enabledModIds.Contains(entry.Id);
                if ((state == ModState.Enabled) != enable)
                {
                    moves.Add((entry, enable));
                }
            }

            if (moves.Count == 0)
            {
                _logger.LogDebug("Apply: nothing to move");
                return;
            }

            _logger.LogInformation("Apply: enabling {Enable} and disabling {Disable} mod(s)", moves.Count(m => m.Enable), moves.Count(m => !m.Enable));
            var done = new List<(ModEntry Entry, bool Enabled)>();
            try
            {
                foreach (var (entry, enable) in moves)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    progress?.Report($"{(enable ? "Enabling" : "Disabling")} {entry.DisplayName}...");
                    await Task.Run(() => MoveModCore(installation, entry, enable), CancellationToken.None).ConfigureAwait(false);
                    done.Add((entry, enable));
                }

                raise = true;
            }
            catch (Exception ex)
            {
                raise = done.Count > 0;
                var rolledBack = RollBack(installation, done);
                if (ex is OperationCanceledException)
                {
                    _logger.LogInformation("Apply cancelled after {Done} move(s); rolled back: {RolledBack}", done.Count, rolledBack);
                    throw;
                }

                var (failed, enable) = moves[done.Count];
                _logger.LogWarning(ex, "Apply failed at '{Mod}' ({Action}); {Done} earlier move(s) rolled back: {RolledBack}", failed.DisplayName, enable ? "enable" : "disable", done.Count, rolledBack);
                throw new ModApplyException(failed, enable, rolledBack, ex);
            }
            finally
            {
                TryRefreshSnapshot(installation);
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
    }

    /// <summary>Undoes the moves in <paramref name="done"/> in reverse order; true when every one of them was undone.</summary>
    private bool RollBack(GameInstallation installation, List<(ModEntry Entry, bool Enabled)> done)
    {
        var complete = true;
        for (var i = done.Count - 1; i >= 0; i--)
        {
            var (entry, enabled) = done[i];
            try
            {
                MoveModCore(installation, entry, !enabled);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                complete = false;
                _logger.LogError(ex, "Rollback could not move '{Mod}' back", entry.DisplayName);
            }
        }

        return complete;
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

            // A reconcile may have adopted the folder while it was being hashed: that entry is the answer.
            var adopted = _manifest.Mods.FirstOrDefault(m => string.Equals(m.FolderName, folderName, StringComparison.OrdinalIgnoreCase));
            if (adopted is not null && adopted.Source == ModSource.Local)
            {
                _logger.LogInformation("Folder '{Folder}' was adopted automatically while being imported", folderName);
                if (!string.IsNullOrWhiteSpace(displayName) && !string.Equals(adopted.DisplayName, displayName.Trim(), StringComparison.Ordinal))
                {
                    var previous = adopted.DisplayName;
                    adopted.DisplayName = displayName.Trim();
                    await SaveManifestOrRevertAsync(() => adopted.DisplayName = previous, installation, cancellationToken).ConfigureAwait(false);
                }

                entry = adopted;
            }
            else
            {
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

    public async Task ReplaceInstalledAsync(ModEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var installation = RequireInstallation();
            var index = _manifest.Mods.FindIndex(m => m.Id == entry.Id);
            if (index < 0)
            {
                throw new ModNotFoundException($"No installed mod has the id {entry.Id}.");
            }

            var previous = _manifest.Mods[index];
            if (!string.Equals(previous.FolderName, entry.FolderName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"An update must keep the folder name ('{previous.FolderName}' vs '{entry.FolderName}').");
            }

            _manifest.Mods[index] = entry;
            await SaveManifestOrRevertAsync(() => _manifest.Mods[index] = previous, installation, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Updated manifest entry for '{Mod}' ({Folder}): version {Old} -> {New}, {Files} files", entry.DisplayName, entry.FolderName, previous.Version ?? "?", entry.Version ?? "?", entry.Files.Count);
        }
        finally
        {
            _gate.Release();
        }

        RaiseChanged();
    }

    public IDisposable ReserveFolderName(string folderName)
    {
        if (!IsPlainFolderName(folderName))
        {
            throw new ArgumentException($"'{folderName}' is not a valid folder name.", nameof(folderName));
        }

        lock (_reservedLock)
        {
            _reserved.Add(folderName);
        }

        return new Reservation(this, folderName);
    }

    private void ReleaseReservation(string folderName)
    {
        lock (_reservedLock)
        {
            _reserved.Remove(folderName);
        }
    }

    private bool IsReserved(string folderName)
    {
        lock (_reservedLock)
        {
            return _reserved.Contains(folderName);
        }
    }

    private sealed class Reservation : IDisposable
    {
        private readonly string _folderName;
        private ModService? _owner;

        public Reservation(ModService owner, string folderName)
        {
            _owner = owner;
            _folderName = folderName;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.ReleaseReservation(_folderName);
        }
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

    /// <summary>Ensures both mod folders exist, rebuilds the snapshots and adopts new folders. Runs under the gate.</summary>
    private async Task<List<ModEntry>> ReconcileCoreAsync(GameInstallation installation, CancellationToken cancellationToken)
    {
        await Task.Run(
            () =>
            {
                EnsureDirectory(installation.ModsDirectory);
                EnsureDirectory(installation.DisabledModsDirectory);
                CleanupInternalFolders(installation);
                RefreshSnapshotCore(installation);
            },
            cancellationToken).ConfigureAwait(false);

        if (!_options.AdoptUnmanagedFolders)
        {
            return new List<ModEntry>();
        }

        return await AdoptNewFoldersAsync(installation, cancellationToken).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Adoption of mods installed by hand
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Records every folder in <c>~mods</c> (and every orphan in the disabled folder) that has no manifest entry as a
    /// local mod. Folders that cannot be read right now (still being copied, locked) stay unmanaged and are tried
    /// again on the next reconcile. Runs under the gate.
    /// </summary>
    private async Task<List<ModEntry>> AdoptNewFoldersAsync(GameInstallation installation, CancellationToken cancellationToken)
    {
        if (_options.AdoptLooseFiles)
        {
            await Task.Run(() => WrapLooseFiles(installation), cancellationToken).ConfigureAwait(false);
        }

        var candidates = new List<(string Name, string Path, bool Enabled)>();
        foreach (var name in ListUnmanagedFolders(installation.ModsDirectory))
        {
            candidates.Add((name, Path.Combine(installation.ModsDirectory, name), true));
        }

        foreach (var name in ListUnmanagedFolders(installation.DisabledModsDirectory))
        {
            if (candidates.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                _logger.LogWarning(
                    "Folder '{Folder}' exists in both {Mods} and {Disabled}; the copy in ~mods is adopted and the other one is left untouched",
                    name,
                    installation.ModsDirectory,
                    installation.DisabledModsDirectory);
                continue;
            }

            candidates.Add((name, Path.Combine(installation.DisabledModsDirectory, name), false));
        }

        var adopted = new List<ModEntry>();
        foreach (var (name, path, enabled) in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsReserved(name))
            {
                continue;
            }

            try
            {
                var files = await FileHasher.HashDirectoryAsync(path, cancellationToken).ConfigureAwait(false);
                var now = DateTimeOffset.UtcNow;
                var entry = new ModEntry
                {
                    FolderName = name,
                    DisplayName = name,
                    Source = ModSource.Local,
                    InstalledAt = now,
                    UpdatedAt = now,
                    Files = files,
                };
                _manifest.Mods.Add(entry);
                adopted.Add(entry);
                _logger.LogInformation("Adopted '{Folder}' found in {Where} as a local mod ({Files} files)", name, enabled ? "~mods" : "the disabled folder", files.Count);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Folder '{Folder}' in {Path} could not be read yet; it stays unmanaged until the next check", name, path);
            }
        }

        if (adopted.Count > 0)
        {
            try
            {
                await _manifestStore.SaveAsync(_manifest, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Keep memory consistent with disk: the folders stay unmanaged and the Import button reports the problem.
                _logger.LogError(ex, "The manifest could not be saved after adopting {Count} folder(s); they stay unmanaged", adopted.Count);
                foreach (var entry in adopted)
                {
                    _manifest.Mods.Remove(entry);
                }

                adopted.Clear();
            }
        }

        RefreshSnapshotCore(installation);
        return adopted;
    }

    /// <summary>Plain-named folders directly inside <paramref name="parent"/> that no manifest entry owns, excluding internal and reserved ones.</summary>
    private IReadOnlyList<string> ListUnmanagedFolders(string parent)
    {
        var managed = new HashSet<string>(_manifest.Mods.Select(m => m.FolderName), StringComparer.OrdinalIgnoreCase);
        List<string> names;
        try
        {
            if (!Directory.Exists(parent))
            {
                return Array.Empty<string>();
            }

            names = Directory.EnumerateDirectories(parent).Select(Path.GetFileName).Where(n => n is not null).Select(n => n!).ToList();
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new ModAccessDeniedException(parent, ex);
        }

        return names
            .Where(name => IsPlainFolderName(name) && !managed.Contains(name) && !IsInternalFolder(name) && !IsReserved(name))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Moves pak sets that lie directly in <c>~mods</c> (<c>Name_P.pak</c> + <c>.ucas</c> + <c>.utoc</c>, or a lone
    /// <c>.pak</c>) into a folder named after the pak so they can be managed. Incomplete sets are left alone; a
    /// set whose files cannot all be moved is put back where it was.
    /// </summary>
    private void WrapLooseFiles(GameInstallation installation)
    {
        List<string> files;
        try
        {
            files = Directory.EnumerateFiles(installation.ModsDirectory)
                .Where(InstallSource.IsModFile)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Loose files in {Mods} could not be listed", installation.ModsDirectory);
            return;
        }

        if (files.Count == 0)
        {
            return;
        }

        foreach (var group in files.GroupBy(f => Path.GetFileNameWithoutExtension(f) ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            var extensions = group.Select(f => Path.GetExtension(f).ToLowerInvariant()).ToHashSet();
            var complete = extensions.Contains(".pak") && extensions.Contains(".ucas") && extensions.Contains(".utoc");
            var pakOnly = extensions.Count == 1 && extensions.Contains(".pak");
            if (!complete && !pakOnly)
            {
                _logger.LogWarning("Loose mod files for '{Base}' in ~mods are incomplete ({Extensions}); leaving them where they are", group.Key, string.Join(", ", extensions));
                continue;
            }

            var baseName = group.Key.Length == 0 ? "Mod" : group.Key;
            var display = baseName.EndsWith("_P", StringComparison.OrdinalIgnoreCase) && baseName.Length > 2 ? baseName[..^2] : baseName;
            var folderName = FolderNameSanitizer.MakeUnique(
                FolderNameSanitizer.Sanitize(display),
                candidate => Directory.Exists(Path.Combine(installation.ModsDirectory, candidate))
                             || Directory.Exists(Path.Combine(installation.DisabledModsDirectory, candidate))
                             || _manifest.Mods.Any(m => string.Equals(m.FolderName, candidate, StringComparison.OrdinalIgnoreCase)));
            var target = Path.Combine(installation.ModsDirectory, folderName);

            var moved = new List<(string From, string To)>();
            try
            {
                Directory.CreateDirectory(target);
                foreach (var file in group)
                {
                    var to = Path.Combine(target, Path.GetFileName(file));
                    File.Move(file, to);
                    moved.Add((file, to));
                }

                _logger.LogInformation("Moved loose mod files {Files} from ~mods into the folder '{Folder}'", group.Select(Path.GetFileName).ToList(), folderName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Loose mod files for '{Base}' could not be moved into '{Folder}'; putting them back", baseName, folderName);
                foreach (var (from, to) in moved)
                {
                    try
                    {
                        File.Move(to, from);
                    }
                    catch (Exception undoError) when (undoError is IOException or UnauthorizedAccessException)
                    {
                        _logger.LogError(undoError, "Could not move {File} back to ~mods", to);
                    }
                }

                try
                {
                    if (Directory.Exists(target) && !Directory.EnumerateFileSystemEntries(target).Any())
                    {
                        Directory.Delete(target);
                    }
                }
                catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException)
                {
                    _logger.LogDebug(cleanupError, "Empty folder {Folder} could not be removed", target);
                }
            }
        }
    }

    /// <summary>Prefix of the app's own temporary folders (install staging); never shown as mods, deleted when stale.</summary>
    public const string InternalFolderPrefix = ".dml-";

    /// <summary>Internal folders younger than this are left alone by <see cref="CleanupInternalFolders"/>.</summary>
    private static readonly TimeSpan InternalFolderGraceperiod = TimeSpan.FromHours(1);

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
                    // Only folders that are clearly abandoned: a second app instance may be building one right now.
                    if (Directory.GetLastWriteTimeUtc(directory) > DateTime.UtcNow - InternalFolderGraceperiod)
                    {
                        continue;
                    }

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
                    .Where(name => !string.IsNullOrEmpty(name) && !managed.Contains(name) && !IsInternalFolder(name) && !IsReserved(name))
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

    internal static void ClearReadOnlyAttributes(string directory)
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
