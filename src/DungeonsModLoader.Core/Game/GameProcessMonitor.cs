using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace DungeonsModLoader.Core.Game;

/// <summary>
/// Polls <see cref="Process.GetProcessesByName(string)"/> for the installation's executable names on a background
/// task (every 2 s by default) and raises <see cref="GameRunningChanged"/> only when the answer changes.
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

    private string[]? _names;
    private CancellationTokenSource? _loopCts;
    private Task? _loop;
    private volatile bool _isGameRunning;
    private bool _disposed;

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

        CancellationTokenSource cts;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            CancelLoopLocked();
            _names = names;
            cts = new CancellationTokenSource();
            _loopCts = cts;
            _loop = Task.Run(() => RunLoopAsync(names, cts.Token), CancellationToken.None);
        }

        _logger.LogDebug("Watching for game processes {Names} every {Interval}s", string.Join(", ", names), _interval.TotalSeconds);
    }

    public void Stop()
    {
        lock (_gate)
        {
            CancelLoopLocked();
            _names = null;
        }

        SetRunning(false);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        string[]? names;
        CancellationToken loopToken;
        lock (_gate)
        {
            names = _names;
            loopToken = _loopCts?.Token ?? CancellationToken.None;
        }

        if (names is null)
        {
            // Not started: nothing to watch, so the game counts as not running.
            SetRunning(false);
            return;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, loopToken);
        try
        {
            await Task.Run(() => CheckAsync(names, linked.Token), linked.Token).ConfigureAwait(false);
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
            _names = null;
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

    private async Task RunLoopAsync(string[] names, CancellationToken cancellationToken)
    {
        try
        {
            await CheckAsync(names, cancellationToken).ConfigureAwait(false);

            using var timer = new PeriodicTimer(_interval);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await CheckAsync(names, cancellationToken).ConfigureAwait(false);
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

    private async Task CheckAsync(string[] names, CancellationToken cancellationToken)
    {
        await _checkLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            SetRunning(IsAnyRunning(names));
        }
        finally
        {
            _checkLock.Release();
        }
    }

    private bool IsAnyRunning(string[] names)
    {
        foreach (var name in names)
        {
            try
            {
                var processes = Process.GetProcessesByName(name);
                try
                {
                    if (processes.Length > 0)
                    {
                        return true;
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
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
            {
                _logger.LogDebug(ex, "Could not query processes named {Name}", name);
            }
        }

        return false;
    }

    private void SetRunning(bool running)
    {
        bool changed;
        lock (_gate)
        {
            changed = _isGameRunning != running;
            _isGameRunning = running;
        }

        if (!changed)
        {
            return;
        }

        _logger.LogInformation("Game is now {State}", running ? "running" : "not running");
        try
        {
            GameRunningChanged?.Invoke(this, running);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A GameRunningChanged handler threw");
        }
    }
}
