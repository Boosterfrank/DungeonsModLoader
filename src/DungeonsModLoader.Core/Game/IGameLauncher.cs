namespace DungeonsModLoader.Core.Game;

/// <summary>Starts the game for a given installation (Steam URI, Xbox AppUserModelId, or the executable).</summary>
public interface IGameLauncher
{
    /// <summary>Launches the game. Throws <see cref="GameLaunchException"/> with a user-friendly message on failure.</summary>
    Task LaunchAsync(GameInstallation installation, CancellationToken cancellationToken = default);
}

/// <summary>Raised when the game could not be started; <see cref="Exception.Message"/> is safe to show to the user.</summary>
public sealed class GameLaunchException : Exception
{
    public GameLaunchException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
