using DungeonsModLoader.Core;
using DungeonsModLoader.Core.Mods;
using DungeonsModLoader.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace DungeonsModLoader.Core.Tests.Mods;

/// <summary>
/// A file that cannot be read right now (locked by another process) must never be mistaken for a corrupt one:
/// quarantining it would make the next save overwrite the user's real data.
/// </summary>
public class ManifestStoreResilienceTests
{
    [Fact]
    public async Task Locked_manifest_is_reported_not_quarantined()
    {
        var root = NewRoot();
        try
        {
            var paths = new AppPaths(root);
            var store = new JsonManifestStore(paths, NullLogger<JsonManifestStore>.Instance);
            await store.SaveAsync(new Manifest { Mods = { new ModEntry { FolderName = "ModA", DisplayName = "Mod A" } } });

            // Hold the file open without read sharing: every read attempt gets a sharing violation.
            await using (new FileStream(paths.ManifestFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                await Assert.ThrowsAsync<IOException>(() => store.LoadAsync());
            }

            Assert.True(File.Exists(paths.ManifestFile));
            Assert.Empty(Directory.GetFiles(root, "manifest.json.corrupt-*"));

            var reloaded = await store.LoadAsync();
            Assert.Single(reloaded.Mods);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Invalid_json_manifest_is_quarantined_and_replaced_by_an_empty_one()
    {
        var root = NewRoot();
        try
        {
            var paths = new AppPaths(root);
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(paths.ManifestFile, "{ this is not json");
            var store = new JsonManifestStore(paths, NullLogger<JsonManifestStore>.Instance);

            var manifest = await store.LoadAsync();

            Assert.Empty(manifest.Mods);
            Assert.False(File.Exists(paths.ManifestFile));
            Assert.Single(Directory.GetFiles(root, "manifest.json.corrupt-*"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Locked_settings_are_reported_not_quarantined()
    {
        var root = NewRoot();
        try
        {
            var paths = new AppPaths(root);
            var store = new JsonSettingsStore(paths, NullLogger<JsonSettingsStore>.Instance);
            store.Current.FirstRunCompleted = true;
            await store.SaveAsync();

            await using (new FileStream(paths.SettingsFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                await Assert.ThrowsAsync<IOException>(() => store.LoadAsync());
            }

            Assert.True(File.Exists(paths.SettingsFile));
            Assert.Empty(Directory.GetFiles(root, "settings.json.corrupt-*"));

            await store.LoadAsync();
            Assert.True(store.Current.FirstRunCompleted);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "dml-tests", "store-" + Guid.NewGuid().ToString("N"));
}
