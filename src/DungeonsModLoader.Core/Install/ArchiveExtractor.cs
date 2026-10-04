using SharpCompress.Archives;
using SharpCompress.Readers;

namespace DungeonsModLoader.Core.Install;

/// <summary>
/// Extracts .zip / .7z / .rar archives with SharpCompress. Every entry path is validated so an archive can never
/// write outside the destination folder (no absolute paths, no ".." segments).
/// </summary>
public static class ArchiveExtractor
{
    public static Task ExtractAsync(string archivePath, string destination, IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default)
        => Task.Run(() => Extract(archivePath, destination, progress, cancellationToken), cancellationToken);

    private static void Extract(string archivePath, string destination, IProgress<InstallProgress>? progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);
        var destinationRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination)) + Path.DirectorySeparatorChar;
        var archiveName = Path.GetFileName(archivePath);

        IArchive archive;
        try
        {
            archive = ArchiveFactory.Open(archivePath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InstallPackageException(
                $"'{archiveName}' could not be opened. It may be damaged, password protected, or not a .zip, .7z or .rar file.",
                ex);
        }

        using (archive)
        {
            try
            {
                var total = archive.Entries.Count(entry => !entry.IsDirectory);
                var done = 0;

                void Report(string? key)
                {
                    done++;
                    progress?.Report(new InstallProgress(
                        $"Extracting {archiveName} ({done}/{total})",
                        total > 0 ? Math.Min(1.0, done / (double)total) : null));
                }

                if (archive.IsSolid)
                {
                    // Solid archives (7z, rar) must be read sequentially.
                    using var reader = archive.ExtractAllEntries();
                    while (reader.MoveToNextEntry())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (reader.Entry.IsDirectory)
                        {
                            continue;
                        }

                        var target = ResolveTarget(destinationRoot, reader.Entry.Key, archiveName);
                        if (target is null)
                        {
                            continue;
                        }

                        using (var stream = File.Create(target))
                        {
                            reader.WriteEntryTo(stream);
                        }

                        Report(reader.Entry.Key);
                    }
                }
                else
                {
                    foreach (var entry in archive.Entries)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (entry.IsDirectory)
                        {
                            continue;
                        }

                        var target = ResolveTarget(destinationRoot, entry.Key, archiveName);
                        if (target is null)
                        {
                            continue;
                        }

                        using (var stream = File.Create(target))
                        {
                            entry.WriteTo(stream);
                        }

                        Report(entry.Key);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not InstallPackageException)
            {
                throw new InstallPackageException(
                    $"'{archiveName}' could not be extracted. It may be damaged or password protected.",
                    ex);
            }
        }
    }

    /// <summary>
    /// Maps an entry key to a full path inside the destination, creating the folder. Returns null for entries
    /// without a usable name; throws for paths that would escape the destination.
    /// </summary>
    private static string? ResolveTarget(string destinationRoot, string? key, string archiveName)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        var relative = key.Replace('\\', '/').TrimStart('/');
        if (relative.Length == 0 || relative.EndsWith('/'))
        {
            return null;
        }

        var segments = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment is "." or "..") || Path.IsPathRooted(relative) || relative.Contains(':'))
        {
            throw new InstallPackageException($"'{archiveName}' contains an unsafe path ('{key}') and was not extracted.");
        }

        var full = Path.GetFullPath(Path.Combine(destinationRoot, Path.Combine(segments)));
        if (!full.StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InstallPackageException($"'{archiveName}' contains an unsafe path ('{key}') and was not extracted.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        return full;
    }
}
