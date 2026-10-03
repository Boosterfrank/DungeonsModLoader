namespace DungeonsModLoader.Core.Mods;

public enum ModSource
{
    Local = 0,
    Nexus = 1,
}

/// <summary>Where the mod folder currently is.</summary>
public enum ModState
{
    /// <summary>Folder is inside <c>~mods</c>; the game loads it.</summary>
    Enabled = 0,

    /// <summary>Folder is inside the app's <c>_Disabled</c> folder, outside <c>Paks</c>.</summary>
    Disabled = 1,

    /// <summary>The manifest knows the mod but its folder is in neither location.</summary>
    Missing = 2,
}

/// <summary>One installed mod as recorded in <c>manifest.json</c> (the single source of truth).</summary>
public sealed class ModEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Folder name under <c>~mods</c> (or the disabled folder). Unique among mods.</summary>
    public required string FolderName { get; set; }

    /// <summary>Name shown in the UI; user-renameable.</summary>
    public required string DisplayName { get; set; }

    public ModSource Source { get; set; } = ModSource.Local;

    public long? NexusModId { get; set; }
    public long? NexusFileId { get; set; }
    public string? Version { get; set; }
    public string? Author { get; set; }
    public string? ThumbnailUrl { get; set; }

    public DateTimeOffset InstalledAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Files inside the mod folder (relative paths with '/' separators) and their SHA-256 hashes.</summary>
    public List<ModFileRecord> Files { get; set; } = new();
}

/// <summary>A file inside a mod folder.</summary>
/// <param name="RelativePath">Path relative to the mod folder, using '/' as separator.</param>
/// <param name="Sha256">Lower-case hex SHA-256 of the file content.</param>
/// <param name="Size">File size in bytes.</param>
public sealed record ModFileRecord(string RelativePath, string Sha256, long Size);

/// <summary>Root document of <c>manifest.json</c>.</summary>
public sealed class Manifest
{
    public int SchemaVersion { get; set; } = 1;
    public List<ModEntry> Mods { get; set; } = new();
}

/// <summary>A manifest entry together with its current on-disk state.</summary>
public sealed class ModInfo
{
    public required ModEntry Entry { get; init; }
    public required ModState State { get; init; }

    /// <summary>Current full path of the mod folder, or null when <see cref="State"/> is Missing.</summary>
    public string? FolderPath { get; init; }

    public bool IsEnabled => State == ModState.Enabled;
    public bool IsMissing => State == ModState.Missing;
}
