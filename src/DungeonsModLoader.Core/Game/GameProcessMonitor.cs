using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace DungeonsModLoader.Core.Game;

/// <summary>
/// Polls <see cref="Process.GetProcessesByName(string)"/> for the installation's executable names on a background
/// task (every 2 s by default) and raises <see cref="GameRunningChanged"/> only when the answer changes. A process
/// only counts when its executable lives inside the installation root (the first Minecraft Dungeons ships the same
/// executable names); when the path cannot be read (elevated process) the name match is trusted.
/// <see cref="Start"/> replaces any previous loop; <see cref="Stop"/> ends it and resets to "not running".
/// Checks never throw: a failing poll is logged and the loop carries on.
/// </summary>
public sealed class GameProcessMonitor : IGameProcessMonitor
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(2);

    private readonly ILogger<GameProcessMonitor> _logger;
    private readonly TimeSpan _interval;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _checkLock = new(1, 1);

    private Watch? _watch;
    private CancellationTokenSource? _loopCts;
    private Task? _loop;
    private volatile bool _isGameRunning;
    private bool _disposed;

    /// <summary>The state subscribers were last told about, and whether a thread is currently publishing.</summary>
    private bool _lastPublished;
    private bool _publishing;

    /// <summary>Incremented by Start/Stop/Dispose so a poll that began before the change can never publish its result.</summary>
    private int _generation;

    public GameProcessMonitor(ILogger<GameProcessMonitor> logger)
        : this(logger, DefaultInterval)
    {
    }

    /// <summary>Uses a custom poll interval (tests).</summary>
    public GameProcessMonitor(ILogger<GameProcessMonitor> logger, TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval), "The poll interval must be positive.");
        }

        _logger = logger;
        _interval = interval;
    }

    public bool IsGameRunning => _isGameRunning;

    public event EventHandler<bool>? GameRunningChanged;

    public void Start(GameInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);

        var names = installation.ExecutableNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (names.Length == 0)
        {
            names = GameInstallation.KnownExecutableNames;
        }

        var watch = new Watch(names, GamePaths.TryNormalize(installation.Root));
        CancellationTokenSource cts;
        int generation;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            CancelLoopLocked();
            generation = ++_generation;
            _watch = watch;
            cts = new CancellationTokenSource();
            _loopCts = cts;
            _loop = Task.Run(() => RunLoopAsync(watch, generation, cts.Token), CancellationToken.None);
        }

        _logger.LogDebug("Watching for game processes {Names} under {Root} every {Interval}s", string.Join(", ", names), watch.Root ?? "(any path)", _interval.TotalSeconds);
    }

    public void Stop()
    {
        int generation;
        lock (_gate)
        {
            CancelLoopLocked();
            _watch = null;
            generation = ++_generation;
        }

        SetRunning(false, generation);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        Watch? watch;
        int generation;
        CancellationToken loopToken;
        lock (_gate)
        {
            watch = _watch;
            generation = _generation;
            loopToken = _loopCts?.Token ?? CancellationToken.None;
        }

        if (watch is null)
        {
            // Not started: nothing to watch, so the game counts as not running.
            SetRunning(false, generation);
            return;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, loopToken);
        try
        {
            await Task.Run(() => CheckAsync(watch, generation, linked.Token), linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The loop was stopped or restarted while refreshing; the new loop reports its own state.
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CancelLoopLocked();
            _watch = null;
            _generation++;
        }

        _checkLock.Dispose();
    }

    private void CancelLoopLocked()
    {
        if (_loopCts is null)
        {
            return;
        }

        try
        {
            _loopCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _loopCts.Dispose();
        _loopCts = null;
        _loop = null;
    }

    private async Task RunLoopAsync(Watch watch, int generation, CancellationToken cancellationToken)
    {
        try
        {
            await CheckAsync(watch, generation, cancellationToken).ConfigureAwait(false);

            using var timer = new PeriodicTimer(_interval);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await CheckAsync(watch, generation, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped or restarted.
        }
        catch (ObjectDisposedException)
        {
            // Disposed while a check was pending.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Game process monitor loop ended unexpectedly");
        }
    }

    private async Task CheckAsync(Watch watch, int generation, CancellationToken cancellationToken)
    {
        await _checkLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var running = IsAnyRunning(watch);
            SetRunning(running, generation);
        }
        finally
        {
            _checkLock.Release();
        }
    }

    private bool IsAnyRunning(Watch watch)
    {
        foreach (var name in watch.Names)
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(name);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or PlatformNotSupportedException)
            {
                _logger.LogDebug(ex, "Could not query processes named {Name}", name);
                continue;
            }

            try
            {
                foreach (var process in processes)
                {
                    if (BelongsToInstallation(process, watch.Root))
                    {
                        return true;
                    }
                }
            }
            finally
            {
                foreach (var process in processes)
                {
                    process.Dispose();
                }
            }
        }

        return false;
    }

    /// <summary>
    /// True when the process runs an executable under <paramref name="root"/>. When the path cannot be read
    /// (access denied for an elevated process, or the process just exited) the name match is trusted.
    /// </summary>
    private static bool BelongsToInstallation(Process process, string? root)
    {
        if (root is null)
        {
            return true;
        }

        string? path;
        try
        {
            path = process.MainModule?.FileName;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return true;
        }

        if (string.IsNullOrEmpty(path))
        {
            return true;
        }

        var normalized = GamePaths.TryNormalize(path);
        if (normalized is null)
        {
            return true;
        }

        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return normalized.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    private void SetRunning(bool running, int generation)
    {
        lock (_gate)
        {
            if (_disposed || generation != _generation)
            {
                // A poll that started before Stop/Start/Dispose must not overwrite the newer state.
                return;
            }

            if (_isGameRunning == running)
            {
                return;
            }

            _isGameRunning = running;
            if (_publishing)
            {
                // Another thread is delivering events right now; it re-reads the state and delivers this change too.
                return;
            }

            _publishing = true;
        }

        PublishChanges();
    }

    /// <summary>
    /// Raises <see cref="GameRunningChanged"/> until subscribers know the current state. Runs outside the lock
    /// (handlers may call back into the monitor), one publisher at a time, always with the latest state: a slow
    /// "running" poll result can therefore never be delivered after a later <see cref="Stop"/> said "not running".
    /// </summary>
    private void PublishChanges()
    {
        while (true)
        {
            bool state;
            lock (_gate)
            {
                state = _isGameRunning;
                if (state == _lastPublished)
                {
                    _publishing = false;
                    return;
                }

                _lastPublished = state;
            }

            try
            {
                _logger.LogInformation("Game is now {State}", state ? "running" : "not running");
                GameRunningChanged?.Invoke(this, state);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "A GameRunningChanged handler threw");
            }
        }
    }

    /// <summary>What one Start() call watches: process names plus the normalized installation root (null = any path).</summary>
    private sealed record Watch(string[] Names, string? Root);
}
