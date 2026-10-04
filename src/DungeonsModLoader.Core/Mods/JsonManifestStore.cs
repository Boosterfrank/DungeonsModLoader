using System.Text.Json;
using DungeonsModLoader.Core.Storage;
using Microsoft.Extensions.Logging;

namespace DungeonsModLoader.Core.Mods;

/// <summary>
/// <see cref="IManifestStore"/> backed by <c>manifest.json</c> in the app data folder. A damaged (unparseable)
/// file is set aside as <c>manifest.json.corrupt-&lt;timestamp&gt;</c> and an empty manifest is returned, so a corrupt
/// manifest can never prevent the app from starting. A file that merely cannot be read right now (locked by a
/// backup or antivirus tool, access denied) is retried briefly and then reported as an error: it must never be
/// replaced by an empty manifest, because the next save would overwrite the user's real mod list.
/// </summary>
public sealed class JsonManifestStore : IManifestStore
{
    private const int ReadAttempts = 4;
    private static readonly TimeSpan ReadRetryDelay = TimeSpan.FromMilliseconds(250);

    private readonly AppPaths _paths;
    private readonly ILogger<JsonManifestStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonManifestStore(AppPaths paths, ILogger<JsonManifestStore> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    /// <summary>Full path of the manifest file this store reads and writes.</summary>
    public string FilePath => _paths.ManifestFile;

    public async Task<Manifest> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Manifest? manifest;
            try
            {
                manifest = await ReadWithRetryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                _logger.LogWarning(ex, "The manifest at {Path} is not valid JSON; it is set aside and an empty manifest is used", FilePath);
                Quarantine(FilePath);
                return new Manifest();
            }

            if (manifest is null)
            {
                _logger.LogDebug("No manifest at {Path}; starting with an empty one", FilePath);
                return new Manifest();
            }

            // A hand-edited file could carry "mods": null; keep the invariant that the list is never null.
            manifest.Mods ??= new List<ModEntry>();
            manifest.Mods.RemoveAll(m => m is null);
            foreach (var mod in manifest.Mods)
            {
                mod.Files ??= new List<ModFileRecord>();
            }

            _logger.LogDebug("Manifest loaded from {Path} ({Count} mods)", FilePath, manifest.Mods.Count);
            return manifest;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(Manifest manifest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await AtomicJsonFile.WriteAsync(FilePath, manifest, cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("Manifest saved to {Path} ({Count} mods)", FilePath, manifest.Mods.Count);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Reads the file, retrying a few times when another process holds it; rethrows the last I/O error.</summary>
    private async Task<Manifest?> ReadWithRetryAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await AtomicJsonFile.ReadAsync<Manifest>(FilePath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= ReadAttempts)
                {
                    _logger.LogError(ex, "The manifest at {Path} could not be read after {Attempts} attempts", FilePath, attempt);
                    throw;
                }

                _logger.LogDebug(ex, "The manifest at {Path} could not be read (attempt {Attempt}); retrying", FilePath, attempt);
                await Task.Delay(ReadRetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private void Quarantine(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            var target = path + ".corrupt-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
            File.Move(path, target, overwrite: true);
            _logger.LogInformation("Corrupt manifest moved to {Target}", target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not set aside the corrupt manifest at {Path}", path);
        }
    }
}
