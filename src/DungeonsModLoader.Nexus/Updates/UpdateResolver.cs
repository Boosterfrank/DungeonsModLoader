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
            return null;
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
        var newestMain = files.Files
            .Where(f => f.Category == NexusFileCategory.Main && f.IsDownloadable)
            .OrderByDescending(f => f.UploadedAt)
            .ThenByDescending(f => f.FileId)
            .FirstOrDefault();
        if (newestMain is null || newestMain.FileId == installedFileId.Value)
        {
            return null;
        }

        if (!byId.TryGetValue(installedFileId.Value, out var installed))
        {
            // The installed file is no longer listed at all: the current main file replaces it.
            return newestMain;
        }

        var replaceable = installed.Category is NexusFileCategory.Main or NexusFileCategory.Update or NexusFileCategory.OldVersion or NexusFileCategory.Removed or NexusFileCategory.Archived;
        return replaceable && newestMain.UploadedAt > installed.UploadedAt ? newestMain : null;
    }
}
