namespace DungeonsModLoader.Nexus.Api;

/// <summary>A mod as shown on the Browse page and in the detail panel (merged view of the v1 and v2 shapes).</summary>
public sealed record NexusMod(
    long ModId,
    string Name,
    string Summary,
    string? Description,
    string? Author,
    string? Uploader,
    string? Version,
    string? PictureUrl,
    string? ThumbnailUrl,
    int Endorsements,
    int Downloads,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string Status,
    bool Available,
    bool AdultContent,
    string? Category,
    bool DirectDownloadEnabled)
{
    public string PageUrl => NexusConstants.ModPageUrl(ModId);

    public string FilesUrl => NexusConstants.ModFilesUrl(ModId);

    /// <summary>Returns a copy with the long description filled in (lists do not carry it).</summary>
    public NexusMod WithDescription(string? description) => this with { Description = description };
}

/// <summary>Nexus file categories (v1 <c>category_id</c>; v2 enum names in comments).</summary>
public enum NexusFileCategory
{
    Unknown = 0,
    Main = 1,          // MAIN
    Update = 2,        // UPDATE ("patch")
    Optional = 3,      // OPTIONAL
    OldVersion = 4,    // OLD_VERSION
    Miscellaneous = 5, // MISCELLANEOUS
    Removed = 6,       // REMOVED ("deleted")
    Archived = 7,      // ARCHIVED
}

/// <summary>One downloadable file of a mod.</summary>
/// <param name="FileName">Archive file name as uploaded (v1 only; null from v2, where the download link reveals it).</param>
public sealed record NexusFile(
    long FileId,
    string Name,
    string Version,
    NexusFileCategory Category,
    bool IsPrimary,
    long SizeBytes,
    string? FileName,
    DateTimeOffset UploadedAt,
    string? ModVersion,
    string? Description,
    string? ChangelogHtml)
{
    /// <summary>Files the user can still download (removed and archived ones are listed on the site only).</summary>
    public bool IsDownloadable => Category is not (NexusFileCategory.Removed or NexusFileCategory.Archived);
}

/// <summary>"File B is a newer version of file A" link set by the author (v1 <c>file_updates</c>).</summary>
public sealed record NexusFileUpdate(long OldFileId, long NewFileId, string? OldFileName, string? NewFileName, DateTimeOffset UploadedAt);

public sealed record NexusFileList(IReadOnlyList<NexusFile> Files, IReadOnlyList<NexusFileUpdate> Updates)
{
    public static NexusFileList Empty { get; } = new(Array.Empty<NexusFile>(), Array.Empty<NexusFileUpdate>());
}

/// <summary>A download location returned by <c>download_link.json</c>; the first one is the user's preferred CDN.</summary>
public sealed record NexusDownloadLink(string Name, string ShortName, Uri Uri);

/// <summary>Entry of <c>updated.json</c>: a mod that changed within the requested period.</summary>
public sealed record NexusUpdatedMod(long ModId, DateTimeOffset LatestFileUpdate, DateTimeOffset LatestModActivity);

/// <summary>The account behind an API key (<c>/v1/users/validate.json</c>).</summary>
public sealed record NexusUser(long UserId, string Name, bool IsPremium, bool IsSupporter, string? Email, string? AvatarUrl);

/// <summary>A mod this mod requires (v2 <c>modRequirements</c>). <paramref name="ModId"/> is null for off-site requirements.</summary>
public sealed record NexusRequirement(long? ModId, string Name, string? Url, string? Notes, bool IsExternal);

/// <summary>One page of a mod list or search.</summary>
public sealed record NexusModPage(IReadOnlyList<NexusMod> Items, int TotalCount, int Offset)
{
    public bool HasMore => Offset + Items.Count < TotalCount;

    public static NexusModPage Empty { get; } = new(Array.Empty<NexusMod>(), 0, 0);
}

/// <summary>The three browse lists of the spec.</summary>
public enum NexusListKind
{
    Trending,
    LatestAdded,
    RecentlyUpdated,
}

/// <summary>Period accepted by <c>updated.json</c>.</summary>
public enum NexusUpdatePeriod
{
    Day,
    Week,
    Month,
}
