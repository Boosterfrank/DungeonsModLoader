using DungeonsModLoader.Nexus.Api;

namespace DungeonsModLoader.Nexus.Updates;

/// <summary>A newer file is available on Nexus for an installed mod.</summary>
/// <param name="ModId">Manifest id of the installed mod.</param>
/// <param name="NewFile">The file to download (MAIN category, or the end of the file-update chain).</param>
public sealed record NexusModUpdate(Guid ModId, long NexusModId, long? InstalledFileId, string? InstalledVersion, NexusFile NewFile, DateTimeOffset DetectedAt)
{
    public string NewVersion => string.IsNullOrWhiteSpace(NewFile.Version) ? NewFile.ModVersion ?? "new version" : NewFile.Version;
}

/// <summary>
/// Finds installed Nexus mods with newer files: <c>updated.json</c> (one request) narrows the candidates, then
/// each candidate's file list is compared with the installed file id through the author's file-update chains and
/// the MAIN category. Runs on startup at most once an hour, and on demand.
/// </summary>
public interface IModUpdateChecker
{
    /// <summary>Known updates by manifest mod id.</summary>
    IReadOnlyDictionary<Guid, NexusModUpdate> Updates { get; }

    DateTimeOffset? LastCheckUtc { get; }

    bool IsChecking { get; }

    /// <summary>User-facing text of the last failed check, or null.</summary>
    string? LastError { get; }

    /// <summary>Raised when <see cref="Updates"/>, <see cref="IsChecking"/> or <see cref="LastError"/> changed. May be raised on any thread.</summary>
    event EventHandler? Changed;

    /// <summary>
    /// Checks every installed Nexus mod. Without <paramref name="force"/> the check is skipped when the last one
    /// is less than an hour old. Returns the updates found; throws <see cref="NexusException"/> when Nexus could
    /// not be asked at all (per-mod failures are logged and skipped).
    /// </summary>
    Task<IReadOnlyList<NexusModUpdate>> CheckAsync(bool force = false, CancellationToken cancellationToken = default);

    /// <summary>Checks one mod right away (context menu "Check for update").</summary>
    Task<NexusModUpdate?> CheckModAsync(Guid modId, CancellationToken cancellationToken = default);

    /// <summary>Forgets the update for a mod (after it was installed or the mod was removed).</summary>
    void Clear(Guid modId);
}
