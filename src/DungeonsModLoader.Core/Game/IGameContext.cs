namespace DungeonsModLoader.Core.Game;

/// <summary>The installation the app is currently managing (null until first-run setup completes).</summary>
public interface IGameContext
{
    GameInstallation? Current { get; }

    bool IsConfigured { get; }

    /// <summary>Raised after <see cref="Current"/> changes (on the caller's thread).</summary>
    event EventHandler? Changed;

    void Set(GameInstallation? installation);
}
