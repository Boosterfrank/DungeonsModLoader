namespace DungeonsModLoader.Core.Game;

/// <summary>
/// Watches whether the game process is running so the app can lock mod-changing actions while it is.
/// Polls by process name (<see cref="GameInstallation.ExecutableNames"/>).
/// </summary>
public interface IGameProcessMonitor : IDisposable
{
    bool IsGameRunning { get; }

    /// <summary>Raised when <see cref="IsGameRunning"/> changes. May be raised on a thread-pool thread.</summary>
    event EventHandler<bool>? GameRunningChanged;

    /// <summary>Starts (or restarts) polling for the given installation's executables.</summary>
    void Start(GameInstallation installation);

    void Stop();

    /// <summary>Re-checks immediately instead of waiting for the next poll.</summary>
    Task RefreshAsync(CancellationToken cancellationToken = default);
}
