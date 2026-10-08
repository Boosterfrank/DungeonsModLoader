using DungeonsModLoader.Nexus.Api;

namespace DungeonsModLoader.Nexus.Updates;

/// <summary>Pure decision logic: which file of a mod, if any, supersedes the installed one.</summary>
public static class UpdateResolver
{
    /// <summary>
    /// Follows the author's "newer version of" chain from the installed file; when there is none, falls back to
    /// the newest MAIN file when the installed file was itself a main / update / old-version file (optional files
    /// are never auto-suggested). Returns null when nothing newer is known.
    /// </summary>
    public static NexusFile? FindNewerFile(long? installedFileId, NexusFileList files)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (installedFileId is null)
        {
            // Linked to its page without knowing the installed version: the current main file is the one to have.
            return NewestMain(files);
        }

        var byId = new Dictionary<long, NexusFile>();
        foreach (var file in files.Files)
        {
            byId[file.FileId] = file;
        }

        // 1. The explicit chain (file_updates): old -> new -> newer ...
        var current = installedFileId.Value;
        var seen = new HashSet<long> { current };
        while (true)
        {
            var next = files.Updates
                .Where(u => u.OldFileId == current)
                .OrderByDescending(u => u.UploadedAt)
                .FirstOrDefault();
            if (next is null || !seen.Add(next.NewFileId))
            {
                break;
            }

            current = next.NewFileId;
        }

        if (current != installedFileId.Value && byId.TryGetValue(current, out var chained) && chained.IsDownloadable)
        {
            return chained;
        }

        // 2. No chain: compare with the newest MAIN file.
        var newestMain = NewestMain(files);
        if (newestMain is null || newestMain.FileId == installedFileId.Value)
        {
            return null;
        }

        if (!byId.TryGetValue(installedFileId.Value, out var installed))
        {
            // The installed file is no longer listed at all: the current main file replaces it.
            return newestMain;
        }

        return IsReplaceable(installed) && newestMain.UploadedAt > installed.UploadedAt ? newestMain : null;
    }

    /// <summary>
    /// True when installing <paramref name="candidate"/> over the installed file is an update of the same mod
    /// rather than another flavour of it: the file the author marked as its successor, a main file uploaded after
    /// the installed one, or any main file when the installed version is unknown (a mod linked by hand) or no
    /// longer listed. Optional and miscellaneous files never count, so installing them still asks.
    /// </summary>
    public static bool IsNewerVersion(long? installedFileId, NexusFile candidate, NexusFileList files)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(files);
        if (!candidate.IsDownloadable || candidate.FileId == installedFileId)
        {
            return false;
        }

        if (installedFileId is null)
        {
            return candidate.Category == NexusFileCategory.Main;
        }

        if (FindNewerFile(installedFileId, files)?.FileId == candidate.FileId)
        {
            return true;
        }

        if (candidate.Category != NexusFileCategory.Main)
        {
            return false;
        }

        var installed = files.Files.FirstOrDefault(f => f.FileId == installedFileId.Value);
        return installed is null || (IsReplaceable(installed) && candidate.UploadedAt > installed.UploadedAt);
    }

    private static NexusFile? NewestMain(NexusFileList files) =>
        files.Files
            .Where(f => f.Category == NexusFileCategory.Main && f.IsDownloadable)
            .OrderByDescending(f => f.UploadedAt)
            .ThenByDescending(f => f.FileId)
            .FirstOrDefault();

    /// <summary>Files that a newer main file supersedes (optional and miscellaneous files live beside the main one).</summary>
    private static bool IsReplaceable(NexusFile installed) =>
        installed.Category is NexusFileCategory.Main or NexusFileCategory.Update or NexusFileCategory.OldVersion or NexusFileCategory.Removed or NexusFileCategory.Archived;
}
