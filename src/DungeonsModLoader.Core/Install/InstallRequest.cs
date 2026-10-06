using DungeonsModLoader.Core.Mods;

namespace DungeonsModLoader.Core.Install;

/// <summary>How to proceed when the target folder already exists.</summary>
public enum ConflictResolution
{
    /// <summary>Stop and report the conflict (<see cref="InstallConflictException"/>) so the UI can ask the user.</summary>
    Ask = 0,

    /// <summary>
    /// Replace what is there: an unmanaged folder is deleted; a managed mod is uninstalled first and the new mod
    /// keeps its display name and enabled/disabled state.
    /// </summary>
    Replace = 1,

    /// <summary>Install next to the existing folder under a suffixed folder name ("Name (2)").</summary>
    KeepBoth = 2,
}

/// <summary>Where a package came from, recorded in the manifest entry (Nexus ids drive update checks and profile sharing).</summary>
public sealed record ModMetadata(
    ModSource Source,
    long? NexusModId = null,
    long? NexusFileId = null,
    string? Version = null,
    string? Author = null,
    string? ThumbnailUrl = null)
{
    public static ModMetadata Local { get; } = new(ModSource.Local);
}

/// <summary>What to install from an inspected plan.</summary>
/// <param name="Plan">The inspected package.</param>
/// <param name="SelectedFileSets">File sets to install (a subset of <see cref="InstallPlan.AllFileSets"/>); invalid sets are skipped.</param>
/// <param name="DisplayName">Name shown in the mod list; also the basis of the folder name.</param>
/// <param name="Conflict">What to do when the folder already exists.</param>
/// <param name="Metadata">Source information for the manifest entry; null means a local mod with the version from the archive name.</param>
/// <param name="UpdateOf">
/// When set, the package replaces this installed mod in place: same folder, id, display name and enabled state;
/// the previous folder is kept as a backup and restored if the swap fails. <paramref name="Conflict"/> is ignored.
/// </param>
public sealed record InstallRequest(
    InstallPlan Plan,
    IReadOnlyList<ModFileSet> SelectedFileSets,
    string DisplayName,
    ConflictResolution Conflict = ConflictResolution.Ask,
    ModMetadata? Metadata = null,
    Guid? UpdateOf = null);

public enum InstallConflictKind
{
    /// <summary>A folder with that name exists in ~mods but no manifest entry owns it.</summary>
    UnmanagedFolder,

    /// <summary>A managed mod already uses that folder (enabled or disabled).</summary>
    ManagedMod,
}

/// <summary>Raised by <see cref="IModInstaller.InstallAsync"/> with <see cref="ConflictResolution.Ask"/> when the target folder exists.</summary>
public sealed class InstallConflictException : Exception
{
    public InstallConflictException(InstallConflictKind kind, string folderName, ModEntry? existingMod)
        : base(kind == InstallConflictKind.ManagedMod
            ? $"A mod already uses the folder '{folderName}'."
            : $"A folder named '{folderName}' already exists in ~mods.")
    {
        Kind = kind;
        FolderName = folderName;
        ExistingMod = existingMod;
    }

    public InstallConflictKind Kind { get; }

    public string FolderName { get; }

    /// <summary>The managed mod that owns the folder (for <see cref="InstallConflictKind.ManagedMod"/>).</summary>
    public ModEntry? ExistingMod { get; }
}

/// <summary>The package contains no usable mod files.</summary>
public sealed class InstallPackageException : Exception
{
    public InstallPackageException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>Progress of an install step. <paramref name="Fraction"/> is 0..1, or null for indeterminate.</summary>
public sealed record InstallProgress(string Message, double? Fraction = null);
