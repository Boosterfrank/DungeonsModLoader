using DungeonsModLoader.Core.Mods;
using DungeonsModLoader.Nexus.Api;
using DungeonsModLoader.Nexus.Nxm;
using DungeonsModLoader.Nexus.Updates;

namespace DungeonsModLoader.App.Services;

/// <summary>
/// Drives every install flow for the UI: local sources (drag &amp; drop, "Install from file"), Nexus downloads
/// (Browse page, <c>nxm://</c> links) and Nexus updates. Inspects with a progress dialog, asks the user to choose
/// when a package offers variants, resolves folder conflicts and permission problems through dialogs, and
/// installs. Every failure becomes a friendly dialog; nothing here throws for expected problems. One flow runs at
/// a time; a second request while one is open is refused with a message. Must be called on the UI thread.
/// </summary>
public interface IInstallCoordinator
{
    /// <summary>Raised on the UI thread after each successful install of a new mod.</summary>
    event EventHandler<ModEntry>? Installed;

    /// <summary>Raised on the UI thread after each successful in-place update.</summary>
    event EventHandler<ModEntry>? Updated;

    /// <summary>
    /// Installs everything in <paramref name="paths"/>: each archive and folder on its own, all loose
    /// .pak/.ucas/.utoc files together. Returns the mods that were installed (cancelled or failed sources are
    /// simply left out).
    /// </summary>
    Task<IReadOnlyList<ModEntry>> InstallFromPathsAsync(IEnumerable<string> paths);

    /// <summary>
    /// Downloads and installs a Nexus file. Premium accounts (and <c>nxm://</c> links carrying the website token)
    /// download directly; free accounts without a token are sent to the file's page on Nexus Mods. When the mod is
    /// already installed the user chooses between replacing it in place and installing separately.
    /// </summary>
    Task<ModEntry?> InstallFromNexusAsync(NexusMod mod, NexusFile file, NxmLink? link = null);

    /// <summary>Downloads the newer file and replaces the installed mod in place (same folder, id, name, state; backup kept).</summary>
    Task<ModEntry?> UpdateFromNexusAsync(NexusModUpdate update);

    /// <summary>Updates one mod after another; returns how many succeeded.</summary>
    Task<int> UpdateAllFromNexusAsync(IReadOnlyList<NexusModUpdate> updates);

    /// <summary>Handles an <c>nxm://</c> link from the website's "Mod Manager Download" button (or the command line).</summary>
    Task HandleNxmLinkAsync(NxmLink link);
}
