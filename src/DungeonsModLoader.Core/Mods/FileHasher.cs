using System.Security.Cryptography;

namespace DungeonsModLoader.Core.Mods;

/// <summary>
/// Streaming SHA-256 for mod files. A mod folder is opaque (pak sets, data folders, readme files all belong to
/// the mod), so <see cref="HashDirectoryAsync"/> records every file under it without exception.
/// </summary>
public static class FileHasher
{
    private const int BufferSize = 1 << 16;

    /// <summary>Computes the lower-case hex SHA-256 of a file without loading it into memory.</summary>
    public static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await ComputeSha256Async(stream, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Computes the lower-case hex SHA-256 of a stream from its current position to the end.</summary>
    public static async Task<string> ComputeSha256Async(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Enumerates every file under <paramref name="directory"/> (recursively) into <see cref="ModFileRecord"/>s with
    /// '/'-separated paths relative to the folder, sorted by path. Nothing is skipped.
    /// </summary>
    public static async Task<List<ModFileRecord>> HashDirectoryAsync(string directory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var root = Path.GetFullPath(directory);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"The folder '{root}' does not exist.");
        }

        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(full => (Full: full, Relative: ToRelativePath(root, full)))
            .OrderBy(f => f.Relative, StringComparer.Ordinal)
            .ToList();

        var records = new List<ModFileRecord>(files.Count);
        foreach (var (full, relative) in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var size = stream.Length;
            var sha256 = await ComputeSha256Async(stream, cancellationToken).ConfigureAwait(false);
            records.Add(new ModFileRecord(relative, sha256, size));
        }

        return records;
    }

    /// <summary>Path of <paramref name="fullPath"/> relative to <paramref name="root"/>, with '/' separators.</summary>
    public static string ToRelativePath(string root, string fullPath)
    {
        return Path.GetRelativePath(root, fullPath).Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
    }
}
