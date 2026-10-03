namespace DungeonsModLoader.Core.Game;

/// <summary>
/// Finds installations from one source (Steam, Xbox app). <see cref="CompositeGameLocator"/> combines them into
/// the <see cref="IGameLocator"/> the rest of the app uses.
/// </summary>
public interface IGameSourceLocator
{
    GameSource Source { get; }

    /// <summary>Installations from this source. Runs on a background thread. Returns empty (and logs) on any failure.</summary>
    Task<IReadOnlyList<GameInstallation>> LocateAsync(CancellationToken cancellationToken = default);
}
