using DungeonsModLoader.Core.Mods;

namespace DungeonsModLoader.Core.Profiles;

/// <summary>A named set of enabled mods, stored as <c>profiles\&lt;name&gt;.json</c>.</summary>
public sealed class Profile
{
    public int SchemaVersion { get; set; } = 1;

    public required string Name { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>The mods that are enabled in this profile; everything else is disabled when the profile is active.</summary>
    public List<ProfileMod> Mods { get; set; } = new();
}

/// <summary>
/// One mod of a profile. The id ties it to the manifest on this PC; the other fields let an exported profile be
/// matched on another PC (Nexus ids first, then folder and display names).
/// </summary>
public sealed class ProfileMod
{
    public Guid? ModId { get; set; }

    public required string FolderName { get; set; }

    public required string DisplayName { get; set; }

    public ModSource Source { get; set; }

    public long? NexusModId { get; set; }

    public long? NexusFileId { get; set; }

    public string? Version { get; set; }

    public static ProfileMod From(ModEntry entry) => new()
    {
        ModId = entry.Id,
        FolderName = entry.FolderName,
        DisplayName = entry.DisplayName,
        Source = entry.Source,
        NexusModId = entry.NexusModId,
        NexusFileId = entry.NexusFileId,
        Version = entry.Version,
    };

    /// <summary>Copy without the PC-specific id (for export / import).</summary>
    public ProfileMod WithoutId() => new()
    {
        ModId = null,
        FolderName = FolderName,
        DisplayName = DisplayName,
        Source = Source,
        NexusModId = NexusModId,
        NexusFileId = NexusFileId,
        Version = Version,
    };
}

/// <summary>The small <c>.json</c> users share: profile name plus mod names and Nexus ids.</summary>
public sealed class ProfileExportDocument
{
    public const string FormatName = "DungeonsModLoader.Profile";

    public string Format { get; set; } = FormatName;

    public int Version { get; set; } = 1;

    public string Game { get; set; } = AppInfo.GameDisplayName;

    public string? ExportedBy { get; set; }

    public DateTimeOffset ExportedAt { get; set; } = DateTimeOffset.UtcNow;

    public required string Name { get; set; }

    public List<ProfileMod> Mods { get; set; } = new();
}
