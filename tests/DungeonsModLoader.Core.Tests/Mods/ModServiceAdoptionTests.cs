using DungeonsModLoader.Core.Mods;
using Microsoft.Extensions.Logging.Abstractions;

namespace DungeonsModLoader.Core.Tests.Mods;

/// <summary>Mods installed by hand (folders or loose pak sets in ~mods) are picked up without a manual import.</summary>
public class ModServiceAdoptionTests
{
    private static (ModService Service, JsonManifestStore Store) Create(TempGameRoot temp)
    {
        var context = new FakeGameContext(temp.Installation);
        var store = new JsonManifestStore(temp.Paths, NullLogger<JsonManifestStore>.Instance);
        var service = new ModService(context, store, NullLogger<ModService>.Instance);
        return (service, store);
    }

    [Fact]
    public async Task Folders_in_mods_are_adopted_on_initialize()
    {
        using var temp = new TempGameRoot();
        temp.CreateMod("ModB");
        temp.CreateMod("ModA");
        var (service, store) = Create(temp);
        using (service)
        {
            await service.InitializeAsync();

            Assert.Equal(new[] { "ModA", "ModB" }, service.Mods.Select(m => m.Entry.DisplayName));
            Assert.All(service.Mods, m =>
            {
                Assert.Equal(ModState.Enabled, m.State);
                Assert.Equal(ModSource.Local, m.Entry.Source);
                Assert.Equal(3, m.Entry.Files.Count);
            });
            Assert.Empty(service.UnmanagedFolders);
            Assert.Equal(2, (await store.LoadAsync()).Mods.Count);
        }
    }

    [Fact]
    public async Task Orphan_folders_in_the_disabled_folder_are_adopted_as_disabled()
    {
        using var temp = new TempGameRoot();
        temp.CreateMod("Old", enabled: false);
        var (service, _) = Create(temp);
        using (service)
        {
            await service.InitializeAsync();

            var info = Assert.Single(service.Mods);
            Assert.Equal("Old", info.Entry.FolderName);
            Assert.Equal(ModState.Disabled, info.State);
            Assert.Equal(temp.DisabledPath("Old"), info.FolderPath);
        }
    }

    [Fact]
    public async Task Loose_pak_sets_in_mods_are_wrapped_into_folders()
    {
        using var temp = new TempGameRoot();
        foreach (var extension in new[] { ".pak", ".ucas", ".utoc" })
        {
            await File.WriteAllTextAsync(Path.Combine(temp.ModsDirectory, "CoolMod_P" + extension), "x");
        }

        await File.WriteAllTextAsync(Path.Combine(temp.ModsDirectory, "Lone_P.pak"), "lone");
        await File.WriteAllTextAsync(Path.Combine(temp.ModsDirectory, "Broken_P.ucas"), "broken");
        var (service, _) = Create(temp);
        using (service)
        {
            await service.InitializeAsync();

            Assert.Equal(new[] { "CoolMod", "Lone" }, service.Mods.Select(m => m.Entry.FolderName));
            Assert.True(File.Exists(Path.Combine(temp.EnabledPath("CoolMod"), "CoolMod_P.pak")));
            Assert.True(File.Exists(Path.Combine(temp.EnabledPath("CoolMod"), "CoolMod_P.utoc")));
            Assert.True(File.Exists(Path.Combine(temp.EnabledPath("Lone"), "Lone_P.pak")));
            Assert.Equal(3, service.Mods.Single(m => m.Entry.FolderName == "CoolMod").Entry.Files.Count);

            // The incomplete set is not touched.
            Assert.Equal(new[] { "Broken_P.ucas" }, Directory.GetFiles(temp.ModsDirectory).Select(Path.GetFileName));
        }
    }

    [Fact]
    public async Task Reserved_folder_names_are_not_adopted_until_released()
    {
        using var temp = new TempGameRoot();
        var (service, _) = Create(temp);
        using (service)
        {
            await service.InitializeAsync();

            using (service.ReserveFolderName("New"))
            {
                temp.CreateMod("New");
                var result = await service.ReconcileAsync();

                Assert.Empty(result.AdoptedMods);
                Assert.Empty(service.UnmanagedFolders);
                Assert.Empty(service.Mods);
            }

            var afterRelease = await service.ReconcileAsync();

            Assert.Equal("New", Assert.Single(afterRelease.AdoptedMods).FolderName);
            Assert.Single(service.Mods);
        }
    }

    [Fact]
    public async Task Unreadable_folder_stays_unmanaged_until_it_can_be_read()
    {
        using var temp = new TempGameRoot();
        var folder = temp.CreateMod("Busy");
        var (service, _) = Create(temp);
        using (service)
        {
            // A file still being written by another program cannot be hashed yet.
            await using (new FileStream(Path.Combine(folder, "Busy_P.pak"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                await service.InitializeAsync();

                Assert.Empty(service.Mods);
                Assert.Equal(new[] { "Busy" }, service.UnmanagedFolders);
            }

            var result = await service.ReconcileAsync();

            Assert.Equal("Busy", Assert.Single(result.AdoptedMods).FolderName);
            Assert.Empty(service.UnmanagedFolders);
            Assert.Equal(ModState.Enabled, Assert.Single(service.Mods).State);
        }
    }

    [Fact]
    public async Task Reconcile_reports_adopted_mods_and_raises_changed()
    {
        using var temp = new TempGameRoot();
        var (service, _) = Create(temp);
        using (service)
        {
            await service.InitializeAsync();
            var changed = 0;
            service.Changed += (_, _) => Interlocked.Increment(ref changed);

            temp.CreateMod("Late");
            var result = await service.ReconcileAsync();

            Assert.Equal("Late", Assert.Single(result.AdoptedMods).DisplayName);
            Assert.Empty(result.UnmanagedFolders);
            Assert.Equal(1, changed);

            // Nothing new: no event.
            await service.ReconcileAsync();
            Assert.Equal(1, changed);
        }
    }

    [Fact]
    public async Task Manual_import_still_works_for_a_folder_that_appeared_after_the_last_reconcile()
    {
        using var temp = new TempGameRoot();
        var (service, _) = Create(temp);
        using (service)
        {
            await service.InitializeAsync();
            temp.CreateMod("Manual");

            var entry = await service.ImportUnmanagedAsync("Manual", "Nice name");

            Assert.Equal("Nice name", entry.DisplayName);
            Assert.Single(service.Mods);

            // Already managed now: a second import is refused, and a reconcile adopts nothing.
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.ImportUnmanagedAsync("Manual"));
            Assert.Empty((await service.ReconcileAsync()).AdoptedMods);
        }
    }
}
