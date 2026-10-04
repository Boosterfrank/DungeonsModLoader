namespace DungeonsModLoader.Core.Mods;

/// <summary>Result of comparing the manifest with the folders on disk.</summary>
/// <param name="UnmanagedFolders">Folder names inside <c>~mods</c> that no manifest entry owns.</param>
/// <param name="MissingMods">Manifest entries whose folder exists in neither location.</param>
public sealed record ReconcileResult(IReadOnlyList<string> UnmanagedFolders, IReadOnlyList<ModEntry> MissingMods);

/// <summary>A mod operation failed for one mod (used for bulk operations).</summary>
public sealed record ModOperationFailure(ModEntry Mod, Exception Error);

/// <summary>
/// The mod store: manifest + on-disk state for the current <see cref="Game.IGameContext"/>. All file work is
/// asynchronous; enabling/disabling is a folder move between <c>~mods</c> and the app's disabled folder.
/// Implementations re-initialize themselves when the game context changes.
/// </summary>
public interface IModService
{
    /// <summary>Snapshot of all managed mods with their current state, sorted by display name.</summary>
    IReadOnlyList<ModInfo> Mods { get; }

    /// <summary>Folders inside <c>~mods</c> that are not managed (from the last reconcile), sorted.</summary>
    IReadOnlyList<string> UnmanagedFolders { get; }

    /// <summary>True once <see cref="InitializeAsync"/> completed for the current game installation.</summary>
    bool IsInitialized { get; }

    /// <summary>
    /// The error of the last failed <see cref="InitializeAsync"/> (for example the manifest could not be read or the
    /// mod folders could not be created), or null when the store is initialized or idle. Set before
    /// <see cref="Changed"/> is raised so the UI can show a retry affordance instead of an empty list.
    /// </summary>
    Exception? InitializationError { get; }

    /// <summary>Raised whenever <see cref="Mods"/> or <see cref="UnmanagedFolders"/> changed. May be raised on any thread.</summary>
    event EventHandler? Changed;

    /// <summary>Loads the manifest for the current game installation and reconciles it with disk. No-op when no game is configured.</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>Re-reads the folders on disk and updates states; creates <c>~mods</c> and the disabled folder if missing.</summary>
    Task<ReconcileResult> ReconcileAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves the mod folder into (<paramref name="enabled"/>) or out of <c>~mods</c>, verifying the result.
    /// Throws <see cref="ModAccessDeniedException"/> when Windows denies access and <see cref="ModNotFoundException"/>
    /// when the mod or its folder cannot be found.
    /// </summary>
    Task SetEnabledAsync(Guid modId, bool enabled, CancellationToken cancellationToken = default);

    /// <summary>Enables or disables every (non-missing) mod; returns the ones that failed instead of throwing.</summary>
    Task<IReadOnlyList<ModOperationFailure>> SetAllEnabledAsync(bool enabled, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adopts a folder that already exists inside <c>~mods</c> as a local mod: records its files and hashes in the
    /// manifest. Nothing on disk is moved or deleted.
    /// </summary>
    Task<ModEntry> ImportUnmanagedAsync(string folderName, string? displayName = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a mod whose folder the install pipeline has just placed inside <c>~mods</c>. The entry's
    /// <see cref="ModEntry.FolderName"/> must exist there and must not be used by another entry.
    /// </summary>
    Task AddInstalledAsync(ModEntry entry, CancellationToken cancellationToken = default);

    Task RenameAsync(Guid modId, string displayName, CancellationToken cancellationToken = default);

    /// <summary>Deletes the mod folder (wherever it currently is) and removes the manifest entry. For a missing mod only the entry is removed.</summary>
    Task UninstallAsync(Guid modId, CancellationToken cancellationToken = default);

    /// <summary>Looks a mod up by id, or null.</summary>
    ModInfo? Find(Guid modId);
}

/// <summary>Windows refused access to a mod folder (typically under Program Files without modify rights).</summary>
public sealed class ModAccessDeniedException : Exception
{
    public ModAccessDeniedException(string path, Exception? inner = null)
        : base($"Access to '{path}' was denied.", inner)
    {
        Path = path;
    }

    /// <summary>The folder or file that could not be accessed.</summary>
    public string Path { get; }
}

/// <summary>The mod id is unknown or the mod folder is missing on disk.</summary>
public sealed class ModNotFoundException : Exception
{
    public ModNotFoundException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// A mod operation could not be completed for a reason the user can act on (folder in use, a folder with the
/// same name already exists, the result could not be verified). <see cref="Exception.Message"/> is written for
/// the user and safe to show as-is.
/// </summary>
public sealed class ModOperationException : Exception
{
    public ModOperationException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
