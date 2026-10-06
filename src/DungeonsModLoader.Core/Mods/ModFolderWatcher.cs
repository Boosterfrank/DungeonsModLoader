using DungeonsModLoader.Core.Game;
using Microsoft.Extensions.Logging;

namespace DungeonsModLoader.Core.Mods;

/// <summary>
/// Watches <c>~mods</c> and the disabled folder while the app runs and asks the mod store to reconcile (and so adopt
/// new folders) a moment after something changed there, so a mod the user drops into <c>~mods</c> by hand shows up
/// without a restart.
/// </summary>
public interface IModFolderWatcher : IDisposable
{
    /// <summary>Starts watching the current game installation and follows game-context changes. Idempotent.</summary>
    void Start();

    void Stop();
}

/// <inheritdoc cref="IModFolderWatcher"/>
public sealed class ModFolderWatcher : IModFolderWatcher
{
    /// <summary>Quiet time after the last file-system event before the reconcile runs (copies produce event bursts).</summary>
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(2);

    private readonly IGameContext _game;
    private readonly IModService _mods;
    private readonly ILogger<ModFolderWatcher> _logger;
    private readonly object _lock = new();
    private readonly List<FileSystemWatcher> _watchers = new();
    private Timer? _timer;
    private bool _started;
    private bool _disposed;
    private int _reconcileRunning;
    private volatile bool _reconcileAgain;

    public ModFolderWatcher(IGameContext game, IModService mods, ILogger<ModFolderWatcher> logger)
    {
        _game = game;
        _mods = mods;
        _logger = logger;
    }

    public void Start()
    {
        lock (_lock)
        {
            if (_disposed || _started)
            {
                return;
            }

            _started = true;
            _game.Changed += OnGameChanged;
            Attach(_game.Current);
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (!_started)
            {
                return;
            }

            _started = false;
            _game.Changed -= OnGameChanged;
            Detach();
            _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            Stop();
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
    }

    private void OnGameChanged(object? sender, EventArgs e)
    {
        lock (_lock)
        {
            if (!_started)
            {
                return;
            }

            Detach();
            Attach(_game.Current);
        }
    }

    /// <summary>Creates the watchers for both mod folders of <paramref name="installation"/>. Runs under the lock.</summary>
    private void Attach(GameInstallation? installation)
    {
        if (installation is null)
        {
            return;
        }

        foreach (var directory in new[] { installation.ModsDirectory, installation.DisabledModsDirectory })
        {
            try
            {
                // The mod store creates both folders; creating them here too only closes the gap right after a
                // game-folder change, before the store has re-initialized.
                Directory.CreateDirectory(directory);
                var watcher = new FileSystemWatcher(directory)
                {
                    NotifyFilter = NotifyFilters.DirectoryName | NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    IncludeSubdirectories = true,
                    InternalBufferSize = 64 * 1024,
                };
                watcher.Created += OnChanged;
                watcher.Deleted += OnChanged;
                watcher.Changed += OnChanged;
                watcher.Renamed += OnRenamed;
                watcher.Error += OnError;
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
                _logger.LogDebug("Watching {Directory} for mod folder changes", directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException)
            {
                _logger.LogWarning(ex, "Could not watch {Directory}; hand-installed mods there are picked up at the next start", directory);
            }
        }
    }

    private void Detach()
    {
        foreach (var watcher in _watchers)
        {
            try
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                _logger.LogDebug(ex, "Watcher dispose failed");
            }
        }

        _watchers.Clear();
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        if (IsInternal(e.FullPath))
        {
            return;
        }

        Schedule();
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        if (IsInternal(e.FullPath) && IsInternal(e.OldFullPath))
        {
            return;
        }

        Schedule();
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        // Typically a buffer overflow during a large copy: the exact events are lost, so reconcile anyway.
        _logger.LogDebug(e.GetException(), "Mod folder watcher reported an error; scheduling a check");
        Schedule();
    }

    /// <summary>The app's own build/staging folders (".dml-*") change constantly during installs and are never mods.</summary>
    private static bool IsInternal(string path) =>
        path.Contains(Path.DirectorySeparatorChar + ModService.InternalFolderPrefix, StringComparison.OrdinalIgnoreCase)
        || path.Contains(Path.AltDirectorySeparatorChar + ModService.InternalFolderPrefix, StringComparison.OrdinalIgnoreCase);

    private void Schedule()
    {
        lock (_lock)
        {
            if (!_started || _disposed)
            {
                return;
            }

            _timer ??= new Timer(_ => Fire(), null, Timeout.Infinite, Timeout.Infinite);
            _timer.Change(Debounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void Fire()
    {
        _ = ReconcileAsync();
    }

    private async Task ReconcileAsync()
    {
        if (Interlocked.Exchange(ref _reconcileRunning, 1) == 1)
        {
            _reconcileAgain = true;
            return;
        }

        try
        {
            do
            {
                _reconcileAgain = false;
                if (!_mods.IsInitialized)
                {
                    return;
                }

                var result = await _mods.ReconcileAsync().ConfigureAwait(false);
                if (result.AdoptedMods.Count > 0)
                {
                    _logger.LogInformation("Detected {Count} new mod folder(s): {Names}", result.AdoptedMods.Count, result.AdoptedMods.Select(m => m.FolderName).ToList());
                }
            }
            while (_reconcileAgain);
        }
        catch (ObjectDisposedException)
        {
            // Shutting down.
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "The automatic mod folder check failed");
        }
        finally
        {
            Interlocked.Exchange(ref _reconcileRunning, 0);
        }
    }
}
