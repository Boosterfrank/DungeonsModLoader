using DungeonsModLoader.Core.Mods;
using Microsoft.Extensions.Logging.Abstractions;

namespace DungeonsModLoader.Core.Tests.Mods;

public class ModServiceInitializationTests
{
    [Fact]
    public async Task Failed_initialization_clears_state_records_the_error_and_recovers_on_retry()
    {
        using var temp = new TempGameRoot();
        temp.CreateMod("ModA");
        var context = new FakeGameContext(temp.Installation);
        var store = new JsonManifestStore(temp.Paths, NullLogger<JsonManifestStore>.Instance);
        using var service = new ModService(context, store, NullLogger<ModService>.Instance);

        await service.InitializeAsync();
        await service.ImportUnmanagedAsync("ModA");
        Assert.True(service.IsInitialized);
        Assert.Single(service.Mods);

        var changedCount = 0;
        service.Changed += (_, _) => Interlocked.Increment(ref changedCount);

        // The manifest cannot be read while another process holds it exclusively: initialization must fail
        // loudly, forget the previous snapshot (never show stale mods) and remember why.
        await using (new FileStream(temp.Paths.ManifestFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await Assert.ThrowsAsync<IOException>(() => service.InitializeAsync());
        }

        Assert.False(service.IsInitialized);
        Assert.IsAssignableFrom<IOException>(service.InitializationError);
        Assert.Empty(service.Mods);
        Assert.Empty(service.UnmanagedFolders);
        Assert.True(changedCount >= 1);

        // Nothing was lost: the next attempt reads the real manifest again.
        await service.InitializeAsync();
        Assert.True(service.IsInitialized);
        Assert.Null(service.InitializationError);
        Assert.Single(service.Mods);
    }

    [Fact]
    public async Task Failed_manifest_save_undoes_the_in_memory_change()
    {
        using var temp = new TempGameRoot();
        temp.CreateMod("ModA");
        var context = new FakeGameContext(temp.Installation);
        var store = new JsonManifestStore(temp.Paths, NullLogger<JsonManifestStore>.Instance);
        using var service = new ModService(context, store, NullLogger<ModService>.Instance);
        await service.InitializeAsync();
        var entry = await service.ImportUnmanagedAsync("ModA");

        // Make the manifest unwritable: a directory in its place defeats the atomic replace.
        File.Delete(temp.Paths.ManifestFile);
        Directory.CreateDirectory(temp.Paths.ManifestFile);
        try
        {
            await Assert.ThrowsAsync<ModOperationException>(() => service.RenameAsync(entry.Id, "Renamed"));

            Assert.Equal("ModA", service.Find(entry.Id)!.Entry.DisplayName);
        }
        finally
        {
            Directory.Delete(temp.Paths.ManifestFile);
        }
    }
}
