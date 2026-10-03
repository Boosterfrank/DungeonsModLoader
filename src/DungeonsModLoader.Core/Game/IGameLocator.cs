namespace DungeonsModLoader.Core.Game;

/// <summary>Finds Minecraft Dungeons II installations (Steam and Xbox app / Minecraft Launcher).</summary>
public interface IGameLocator
{
    /// <summary>Every installation found on this machine, Steam first. Empty when none is installed. Never throws.</summary>
    Task<IReadOnlyList<GameInstallation>> LocateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Classifies a folder the user picked by hand: returns a Steam/Xbox installation when <paramref name="root"/>
    /// is one of the known installs (so launching keeps working), a <see cref="GameSource.Manual"/> installation
    /// when it is merely a valid game root, or <c>null</c> when it does not contain <c>Dungeons\Content\Paks</c>.
    /// </summary>
    Task<GameInstallation?> IdentifyAsync(string root, CancellationToken cancellationToken = default);
}
