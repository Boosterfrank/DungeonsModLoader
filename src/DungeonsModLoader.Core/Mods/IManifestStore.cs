namespace DungeonsModLoader.Core.Mods;

/// <summary>Loads and atomically saves <c>manifest.json</c>.</summary>
public interface IManifestStore
{
    /// <summary>Returns the manifest, or an empty one when the file does not exist yet.</summary>
    Task<Manifest> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Writes atomically (temp file + <see cref="File.Replace"/>).</summary>
    Task SaveAsync(Manifest manifest, CancellationToken cancellationToken = default);
}
