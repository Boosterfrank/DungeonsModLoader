namespace DungeonsModLoader.Core.Mods;

/// <summary>Behaviour switches of <see cref="ModService"/>.</summary>
public sealed class ModServiceOptions
{
    /// <summary>
    /// Adopt folders inside <c>~mods</c> (and orphans inside the disabled folder) that no manifest entry owns as
    /// local mods during every reconcile, so mods the user installs by hand show up on their own. Default true.
    /// </summary>
    public bool AdoptUnmanagedFolders { get; init; } = true;

    /// <summary>
    /// Wrap <c>.pak/.ucas/.utoc</c> files lying directly in <c>~mods</c> into a folder of their own (named after
    /// the pak) and adopt that folder. Default true; has no effect while <see cref="AdoptUnmanagedFolders"/> is false.
    /// </summary>
    public bool AdoptLooseFiles { get; init; } = true;

    /// <summary>The app's behaviour: everything found in the mod folders is adopted automatically.</summary>
    public static ModServiceOptions Default { get; } = new();

    /// <summary>Nothing is adopted; folders stay "unmanaged" until <see cref="IModService.ImportUnmanagedAsync"/> is called.</summary>
    public static ModServiceOptions ManualImportOnly { get; } = new() { AdoptUnmanagedFolders = false, AdoptLooseFiles = false };
}
