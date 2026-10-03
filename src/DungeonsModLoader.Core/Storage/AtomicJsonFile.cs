using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DungeonsModLoader.Core.Storage;

/// <summary>
/// JSON persistence with atomic writes: the document is written to a temporary sibling file and then swapped
/// into place with <see cref="File.Replace"/> (or <see cref="File.Move"/> when the target does not exist yet),
/// so a crash mid-write can never leave a truncated settings or manifest file behind.
/// </summary>
public static class AtomicJsonFile
{
    /// <summary>Serializer options shared by every JSON file the app writes (camelCase, indented, enums as strings).</summary>
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Reads and deserializes <paramref name="path"/>; returns <c>null</c> when the file does not exist.</summary>
    public static async Task<T?> ReadAsync<T>(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            return default;
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        return await JsonSerializer.DeserializeAsync<T>(stream, Options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Serializes <paramref name="value"/> and atomically replaces <paramref name="path"/> with it.</summary>
    public static async Task WriteAsync<T>(string path, T value, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, value, Options, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
            {
                // File.Replace is atomic on NTFS; the optional backup file is not needed.
                File.Replace(tempPath, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, path);
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (IOException)
                {
                    // Best effort: a stray temp file is harmless and is overwritten/ignored next time.
                }
            }
        }
    }
}
