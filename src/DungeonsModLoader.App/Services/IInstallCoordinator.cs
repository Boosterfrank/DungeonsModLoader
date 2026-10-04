using DungeonsModLoader.Core.Mods;

namespace DungeonsModLoader.App.Services;

/// <summary>
/// Drives the local install flow for the UI: inspects each dropped / picked source with a progress dialog, asks
/// the user to choose when a package offers variants, resolves folder conflicts and permission problems through
/// dialogs, and installs. Every failure becomes a friendly dialog; nothing here throws for expected problems.
/// Must be called on the UI thread.
/// </summary>
public interface IInstallCoordinator
{
    /// <summary>Raised on the UI thread after each successful install.</summary>
    event EventHandler<ModEntry>? Installed;

    /// <summary>
    /// Installs everything in <paramref name="paths"/>: each archive and folder on its own, all loose
    /// .pak/.ucas/.utoc files together. Returns the mods that were installed (cancelled or failed sources are
    /// simply left out).
    /// </summary>
    Task<IReadOnlyList<ModEntry>> InstallFromPathsAsync(IEnumerable<string> paths);
}
