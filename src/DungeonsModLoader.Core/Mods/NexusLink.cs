namespace DungeonsModLoader.Core.Mods;

/// <summary>
/// What linking a mod to its Nexus Mods page records (<see cref="IModService.LinkToNexusAsync"/>): the page, and
/// the installed file when the user knows which version they have.
/// </summary>
/// <param name="NexusModId">The mod's id on Nexus Mods.</param>
/// <param name="NexusFileId">
/// The installed file, or null when the user is not sure which version is installed; the next update check then
/// offers the newest main file, and installing it replaces the folder in place.
/// </param>
/// <param name="Version">Version text of that file; null keeps whatever the entry had.</param>
/// <param name="Author">Author shown on the page; null keeps the entry's author.</param>
/// <param name="ThumbnailUrl">Page picture for the list; null keeps the entry's picture.</param>
/// <param name="Requirements">The page's requirements for dependency hints; null keeps what the entry had.</param>
/// <param name="FileUploadedAt">
/// When that file was uploaded; it becomes the entry's <see cref="ModEntry.UpdatedAt"/> so files uploaded later
/// count as updates. Null uses the time of linking.
/// </param>
public sealed record NexusLink(
    long NexusModId,
    long? NexusFileId = null,
    string? Version = null,
    string? Author = null,
    string? ThumbnailUrl = null,
    IReadOnlyList<ModRequirementRecord>? Requirements = null,
    DateTimeOffset? FileUploadedAt = null);
