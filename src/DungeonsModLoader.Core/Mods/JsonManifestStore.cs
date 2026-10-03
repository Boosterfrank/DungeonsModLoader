using System.Text.Json;
using DungeonsModLoader.Core.Storage;
using Microsoft.Extensions.Logging;

namespace DungeonsModLoader.Core.Mods;

/// <summary>
/// <see cref="IManifestStore"/> backed by <c>manifest.json</c> in the app data folder. Reads never throw for a
/// damaged file: the bad file is set aside as <c>manifest.json.corrupt-&lt;timestamp&gt;</c> and an empty manifest
/// is returned, so a corrupt manifest can never prevent the app from starting.
/// </summary>
public sealed class JsonManifestStore : IManifestStore
{
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
            var manifest = await AtomicJsonFile.ReadAsync<Manifest>(FilePath, cancellationToken).ConfigureAwait(false);
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
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            _logger.LogWarning(ex, "The manifest at {Path} could not be read; it is set aside and an empty manifest is used", FilePath);
            Quarantine(FilePath);
            return new Manifest();
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
