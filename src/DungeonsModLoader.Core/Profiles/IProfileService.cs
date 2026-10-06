namespace DungeonsModLoader.Core.Profiles;

/// <summary>Read-only view of one profile.</summary>
public sealed record ProfileInfo(string Name, bool IsActive, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, IReadOnlyList<ProfileMod> Mods)
{
    public int ModCount => Mods.Count;
}

/// <summary>Outcome of <see cref="IProfileService.ImportAsync"/>.</summary>
/// <param name="Profile">The profile that was created from the file.</param>
/// <param name="MissingMods">Mods the file lists that are not installed on this PC (candidates for a Nexus download).</param>
public sealed record ProfileImportResult(ProfileInfo Profile, IReadOnlyList<ProfileMod> MissingMods);

/// <summary>
/// Profiles: named sets of enabled mods. The active profile always mirrors what is enabled on disk (every toggle
/// and install updates it); switching applies another profile's set by moving folders, with rollback when a move
/// fails. There is always at least one profile ("Default").
/// </summary>
public interface IProfileService
{
    /// <summary>All profiles, "Default" first, then by name.</summary>
    IReadOnlyList<ProfileInfo> Profiles { get; }

    string ActiveProfileName { get; }

    bool IsInitialized { get; }

    /// <summary>Raised whenever <see cref="Profiles"/> or <see cref="ActiveProfileName"/> changed. May be raised on any thread.</summary>
    event EventHandler? Changed;

    /// <summary>Loads the profile files, creates "Default" when missing and records the current enabled set into the active profile.</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    ProfileInfo? Find(string name);

    /// <summary>Creates a profile that starts with the mods currently enabled. Throws <see cref="ProfileException"/> for a bad or taken name.</summary>
    Task<ProfileInfo> CreateAsync(string name, CancellationToken cancellationToken = default);

    Task<ProfileInfo> DuplicateAsync(string sourceName, string newName, CancellationToken cancellationToken = default);

    Task RenameAsync(string name, string newName, CancellationToken cancellationToken = default);

    /// <summary>Deletes a profile. The active profile and the last remaining profile cannot be deleted.</summary>
    Task DeleteAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies the profile (enables its mods, disables all others) and makes it active. Throws
    /// <see cref="Mods.ModApplyException"/> when a move failed; the previous profile stays active in that case.
    /// </summary>
    Task SwitchAsync(string name, IProgress<string>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Writes the profile as a shareable <c>.json</c> (mod names + Nexus ids, no local ids).</summary>
    Task ExportAsync(string name, string filePath, CancellationToken cancellationToken = default);

    /// <summary>Creates a new (inactive) profile from an exported file; a taken name gets a " (2)" suffix.</summary>
    Task<ProfileImportResult> ImportAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>Mods the profile lists that are not installed on this PC.</summary>
    IReadOnlyList<ProfileMod> FindMissingMods(string name);
}

/// <summary>A profile operation was refused for a reason the user can act on; the message is written for the user.</summary>
public sealed class ProfileException : Exception
{
    public ProfileException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
