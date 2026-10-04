using DungeonsModLoader.Core.Game;
using DungeonsModLoader.Core.Mods;
using Microsoft.Extensions.Logging.Abstractions;

namespace DungeonsModLoader.Core.Tests.Mods;

public class ModServiceTests
{
    private static readonly TimeSpan EventTimeout = TimeSpan.FromSeconds(15);

    private static (ModService Service, FakeGameContext Context, JsonManifestStore Store) Create(TempGameRoot temp, bool configured = true)
    {
        var context = new FakeGameContext(configured ? temp.Installation : null);
        var store = new JsonManifestStore(temp.Paths, NullLogger<JsonManifestStore>.Instance);
        var service = new ModService(context, store, NullLogger<ModService>.Instance);
        return (service, context, store);
    }

    private static async Task<ModEntry> ImportAsync(ModService service, string folder, string? displayName = null)
    {
        return await service.ImportUnmanagedAsync(folder, displayName);
    }

    private static Task<EventArgs> NextChangedAsync(ModService service)
    {
        var tcs = new TaskCompletionSource<EventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Changed += Handler;
        return tcs.Task.WaitAsync(EventTimeout);

        void Handler(object? sender, EventArgs e)
        {
            service.Changed -= Handler;
            tcs.TrySetResult(e);
        }
    }

    [Fact]
    public async Task Initialize_without_game_is_a_no_op()
    {
        using var temp = new TempGameRoot();
        var (service, _, _) = Create(temp, configured: false);
        using (service)
        {
            await service.InitializeAsync();

            Assert.False(service.IsInitialized);
            Assert.Empty(service.Mods);
            Assert.Empty(service.UnmanagedFolders);
        }
    }

    [Fact]
    public async Task Initialize_lists_unmanaged_folders_and_no_mods()
    {
        using var temp = new TempGameRoot();
        temp.CreateMod("ModB");
        temp.CreateMod("ModA");
        temp.CreateMod("ModC");
        var (service, _, _) = Create(temp);
        using (service)
        {
            await service.InitializeAsync();

            Assert.True(service.IsInitialized);
            Assert.Empty(service.Mods);
            Assert.Equal(new[] { "ModA", "ModB", "ModC" }, service.UnmanagedFolders);
        }
    }

    [Fact]
    public async Task Reconcile_creates_both_mod_folders()
    {
        using var temp = new TempGameRoot();
        Directory.Delete(temp.ModsDirectory);
        var (service, _, _) = Create(temp);
        using (service)
        {
            var result = await service.ReconcileAsync();

            Assert.True(Directory.Exists(temp.ModsDirectory));
            Assert.True(Directory.Exists(temp.DisabledModsDirectory));
            Assert.Empty(result.UnmanagedFolders);
            Assert.Empty(result.MissingMods);
        }
    }

    [Fact]
    public async Task Import_records_files_and_removes_folder_from_unmanaged()
    {
        using var temp = new TempGameRoot();
        var folder = temp.CreateMod("ModA");
        Directory.CreateDirectory(Path.Combine(folder, "skins"));
        await File.WriteAllTextAsync(Path.Combine(folder, "skins", "abc.json"), "abc");
        temp.CreateMod("ModB");
        var (service, _, store) = Create(temp);
        using (service)
        {
            await service.InitializeAsync();

            var entry = await ImportAsync(service, "ModA", "  My Mod A  ");

            Assert.Equal("ModA", entry.FolderName);
            Assert.Equal("My Mod A", entry.DisplayName);
            Assert.Equal(ModSource.Local, entry.Source);
            Assert.Equal(4, entry.Files.Count);
            Assert.Contains(entry.Files, f => f.RelativePath == "skins/abc.json" && f.Sha256 == "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad" && f.Size == 3);
            Assert.Contains(entry.Files, f => f.RelativePath == "ModA_P.pak");
            Assert.Equal(new[] { "ModB" }, service.UnmanagedFolders);

            var info = Assert.Single(service.Mods);
            Assert.Same(entry, info.Entry);
            Assert.Equal(ModState.Enabled, info.State);
            Assert.Equal(folder, info.FolderPath);
            Assert.Same(info, service.Find(entry.Id));

            var persisted = await store.LoadAsync();
            Assert.Equal(entry.Id, Assert.Single(persisted.Mods).Id);
        }
    }

    [Fact]
    public async Task Import_uses_on_disk_casing_and_defaults_display_name()
    {
        using var temp = new TempGameRoot();
        temp.CreateMod("ModA");
        var (service, _, _) = Create(temp);
        using (service)
        {
            await service.InitializeAsync();

            var entry = await ImportAsync(service, "moda");

            Assert.Equal("ModA", entry.FolderName);
            Assert.Equal("ModA", entry.DisplayName);
        }
    }

    [Fact]
    public async Task Import_refuses_unknown_managed_and_invalid_folders()
    {
        using var temp = new TempGameRoot();
        temp.CreateMod("ModA");
        var (service, _, _) = Create(temp);
        using (service)
        {
            await service.InitializeAsync();
            await ImportAsync(service, "ModA");

            await Assert.ThrowsAsync<ModNotFoundException>(() => ImportAsync(service, "Nope"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => ImportAsync(service, "moda"));
            await Assert.ThrowsAsync<ArgumentException>(() => ImportAsync(service, "..\\ModA"));
            await Assert.ThrowsAsync<ArgumentException>(() => ImportAsync(service, ".."));
        }
    }

    [Fact]
    public async Task SetEnabled_moves_folder_between_locations()
    {
        using var temp = new TempGameRoot();
        temp.CreateMod("ModA");
        var (service, _, _) = Create(temp);
        using (service)
        {
            await service.InitializeAsync();
            var entry = await ImportAsync(service, "ModA");

            await service.SetEnabledAsync(entry.Id, false);

            Assert.False(Directory.Exists(temp.EnabledPath("ModA")));
            Assert.True(Directory.Exists(temp.DisabledPath("ModA")));
            Assert.True(File.Exists(Path.Combine(temp.DisabledPath("ModA"), "ModA_P.pak")));
            var disabled = service.Find(entry.Id)!;
            Assert.Equal(ModState.Disabled, disabled.State);
            Assert.False(disabled.IsEnabled);
            Assert.Equal(temp.DisabledPath("ModA"), disabled.FolderPath);

            // Already disabled: no-op, no exception.
            await service.SetEnabledAsync(entry.Id, false);

            await service.SetEnabledAsync(entry.Id, true);

            Assert.True(Directory.Exists(temp.EnabledPath("ModA")));
            Assert.False(Directory.Exists(temp.DisabledPath("ModA")));
            Assert.Equal(ModState.Enabled, service.Find(entry.Id)!.State);
        }
    }

    [Fact]
    public async Task SetEnabled_refuses_when_target_folder_already_exists()
    {
        using var temp = new TempGameRoot();
        temp.CreateMod("ModA");
        var (service, _, _) = Create(temp);
        using (service)
        {
            await service.InitializeAsync();
            var entry = await ImportAsync(service, "ModA");
            Directory.CreateDirectory(temp.DisabledPath("ModA"));

            var ex = await Assert.ThrowsAsync<ModOperationException>(() => service.SetEnabledAsync(entry.Id, false));

            Assert.Contains("already exists", ex.Message);
            Assert.True(Directory.Exists(temp.EnabledPath("ModA")));
        }
    }

    [Fact]
    public async Task Deleted_folder_becomes_missing_and_cannot_be_toggled()
    {
        using var temp = new TempGameRoot();
        temp.CreateMod("ModA");
        var (service, _, _) = Create(temp);
        using (service)
        {
            await service.InitializeAsync();
            var entry = await ImportAsync(service, "ModA");
            Directory.Delete(temp.EnabledPath("ModA"), recursive: true);

            var result = await service.ReconcileAsync();

            Assert.Equal(entry.Id, Assert.Single(result.MissingMods).Id);
            var info = service.Find(entry.Id)!;
            Assert.Equal(ModState.Missing, info.State);
            Assert.True(info.IsMissing);
            Assert.Null(info.FolderPath);
            await Assert.ThrowsAsync<ModNotFoundException>(() => service.SetEnabledAsync(entry.Id, false));
            await Assert.ThrowsAsync<ModNotFoundException>(() => service.SetEnabledAsync(Guid.NewGuid(), true));
        }
    }

    [Fact]
    public async Task Rename_updates_display_name_and_persists()
    {
        using var temp = new TempGameRoot();
        temp.CreateMod("ModA");
        var (service, _, store) = Create(temp);
        using (service)
        {
            await service.InitializeAsync();
            var entry = await ImportAsync(service, "ModA");
            var before = entry.UpdatedAt;
            await Task.Delay(10);

            await service.RenameAsync(entry.Id, "  Renamed  ");

            Assert.Equal("Renamed", service.Find(entry.Id)!.Entry.DisplayName);
            Assert.True(service.Find(entry.Id)!.Entry.UpdatedAt > before);
            Assert.Equal("Renamed", Assert.Single((await store.LoadAsync()).Mods).DisplayName);
            await Assert.ThrowsAsync<ArgumentException>(() => service.RenameAsync(entry.Id, "   "));
            await Assert.ThrowsAsync<ModNotFoundException>(() => service.RenameAsync(Guid.NewGuid(), "x"));
        }
    }

    [Fact]
    public async Task Mods_are_sorted_by_display_name()
    {
        using var temp = new TempGameRoot();
        temp.CreateMod("ModA");
        temp.CreateMod("ModB");
        var (service, _, _) = Create(temp);
        using (service)
        {
            await service.InitializeAsync();
            await ImportAsync(service, "ModA", "zebra");
            await ImportAsync(service, "ModB", "Apple");

            Assert.Equal(new[] { "Apple", "zebra" }, service.Mods.Select(m => m.Entry.DisplayName));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Uninstall_deletes_folder_and_entry(bool enabled)
    {
        using var temp = new TempGameRoot();
        var folder = temp.CreateMod("ModA");
        File.SetAttributes(Path.Combine(folder, "ModA_P.pak"), FileAttributes.ReadOnly);
        var (service, _, store) = Create(temp);
        using (service)
        {
            await service.InitializeAsync();
            var entry = await ImportAsync(service, "ModA");
            if (!enabled)
            {
                await service.SetEnabledAsync(entry.Id, false);
            }

            await service.UninstallAsync(entry.Id);

            Assert.False(Directory.Exists(temp.EnabledPath("ModA")));
            Assert.False(Directory.Exists(temp.DisabledPath("ModA")));
            Assert.Empty(service.Mods);
            Assert.Null(service.Find(entry.Id));
            Assert.Empty((await store.LoadAsync()).Mods);
        }
    }

    [Fact]
    public async Task Uninstall_of_missing_mod_removes_entry_only()
    {
        using var temp = new TempGameRoot();
        temp.CreateMod("ModA");
        var (service, _, _) = Create(temp);
        using (service)
        {
            await service.InitializeAsync();
            var entry = await ImportAsync(service, "ModA");
            Directory.Delete(temp.EnabledPath("ModA"), recursive: true);
            await service.ReconcileAsync();

            await service.UninstallAsync(entry.Id);

            Assert.Empty(service.Mods);
        }
    }

    [Fact]
    public async Task Uninstall_refuses_tampered_folder_name_outside_mod_folders()
    {
        using var temp = new TempGameRoot();
        var (_, _, store) = Create(temp);
        var gameFile = Path.Combine(temp.PaksDirectory, "Dungeons-Windows.pak");
        await File.WriteAllTextAsync(gameFile, "game data");
        var tampered = new ModEntry { FolderName = "..\\..", DisplayName = "Evil" };
        await store.SaveAsync(new Manifest { Mods = { tampered } });

        var (service, _, _) = Create(temp);
        using (service)
        {
            await service.InitializeAsync();

            Assert.Equal(ModState.Missing, service.Find(tampered.Id)!.State);
            await Assert.ThrowsAsync<ModNotFoundException>(() => service.SetEnabledAsync(tampered.Id, false));

            // A tampered entry is always "missing", so uninstalling it only drops the manifest entry: nothing
            // outside the mod folders is ever touched, and the user is not stuck with an unremovable row.
            await service.UninstallAsync(tampered.Id);

            Assert.True(File.Exists(gameFile));
            Assert.True(Directory.Exists(temp.PaksDirectory));
            Assert.Null(service.Find(tampered.Id));
            Assert.Empty((await store.LoadAsync()).Mods);
        }
    }

    [Fact]
    public void IsDirectlyUnder_only_accepts_immediate_children()
    {
        var parent = Path.Combine(Path.GetTempPath(), "p");

        Assert.True(ModService.IsDirectlyUnder(Path.Combine(parent, "child"), parent));
        Assert.True(ModService.IsDirectlyUnder(Path.Combine(parent, "child") + Path.DirectorySeparatorChar, parent + Path.DirectorySeparatorChar));
        Assert.False(ModService.IsDirectlyUnder(Path.Combine(parent, "child", "grandchild"), parent));
        Assert.False(ModService.IsDirectlyUnder(parent, parent));
        Assert.False(ModService.IsDirectlyUnder(Path.Combine(parent, ".."), parent));
        Assert.False(ModService.IsDirectlyUnder(Path.Combine(parent, "child", ".."), parent));
        Assert.False(ModService.IsDirectlyUnder(Path.GetDirectoryName(parent)!, parent));
    }

    [Theory]
    [InlineData("ModA", true)]
    [InlineData("Mod With Spaces", true)]
    [InlineData("~mods", true)]
    [InlineData("", false)]
    [InlineData("  ", false)]
    [InlineData(".", false)]
    [InlineData("..", false)]
    [InlineData("a/b", false)]
    [InlineData("a\\b", false)]
    [InlineData("C:", false)]
    [InlineData("trailing.", false)]
    [InlineData("trailing ", false)]
    public void IsPlainFolderName_validates(string name, bool expected)
    {
        Assert.Equal(expected, ModService.IsPlainFolderName(name));
    }

    [Fact]
    public async Task SetAllEnabled_toggles_every_mod_and_collects_failures()
    {
        using var temp = new TempGameRoot();
        temp.CreateMod("ModA");
        temp.CreateMod("ModB");
        temp.CreateMod("ModC");
        var (service, _, _) = Create(temp);
        using (service)
        {
            await service.InitializeAsync();
            var a = await ImportAsync(service, "ModA");
            var b = await ImportAsync(service, "ModB");
            var c = await ImportAsync(service, "ModC");

            IReadOnlyList<ModOperationFailure> failures;
            // Hold a file inside ModB open: Windows refuses to rename a folder with an open file in it.
            await using (new FileStream(Path.Combine(temp.EnabledPath("ModB"), "ModB_P.pak"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                failures = await service.SetAllEnabledAsync(false);
            }

            var failure = Assert.Single(failures);
            Assert.Same(b, failure.Mod);
            var inUse = Assert.IsType<ModOperationException>(failure.Error);
            Assert.Contains("in use", inUse.Message);
            Assert.Equal(ModState.Disabled, service.Find(a.Id)!.State);
            Assert.Equal(ModState.Enabled, service.Find(b.Id)!.State);
            Assert.Equal(ModState.Disabled, service.Find(c.Id)!.State);

            failures = await service.SetAllEnabledAsync(false);
            Assert.Empty(failures);
            Assert.All(service.Mods, m => Assert.Equal(ModState.Disabled, m.State));

            failures = await service.SetAllEnabledAsync(true);
            Assert.Empty(failures);
            Assert.All(service.Mods, m => Assert.Equal(ModState.Enabled, m.State));
            Assert.All(new[] { "ModA", "ModB", "ModC" }, name => Assert.True(Directory.Exists(temp.EnabledPath(name))));
        }
    }

    [Fact]
    public async Task Folder_in_both_locations_counts_as_enabled_and_stray_copy_is_untouched()
    {
        using var temp = new TempGameRoot();
        temp.CreateMod("ModA");
        var (service, _, _) = Create(temp);
        using (service)
        {
            await service.InitializeAsync();
            var entry = await ImportAsync(service, "ModA");
            temp.CreateMod("ModA", enabled: false);

            await service.ReconcileAsync();

            var info = service.Find(entry.Id)!;
            Assert.Equal(ModState.Enabled, info.State);
            Assert.Equal(temp.EnabledPath("ModA"), info.FolderPath);
            Assert.True(Directory.Exists(temp.DisabledPath("ModA")));
            Assert.Empty(service.UnmanagedFolders);
        }
    }

    [Fact]
    public async Task Changed_is_raised_for_initialize_import_toggle_rename_and_uninstall()
    {
        using var temp = new TempGameRoot();
        temp.CreateMod("ModA");
        var (service, _, _) = Create(temp);
        using (service)
        {
            var count = 0;
            service.Changed += (_, _) => Interlocked.Increment(ref count);

            await service.InitializeAsync();
            Assert.Equal(1, count);

            var entry = await ImportAsync(service, "ModA");
            Assert.Equal(2, count);

            await service.SetEnabledAsync(entry.Id, false);
            Assert.Equal(3, count);

            await service.RenameAsync(entry.Id, "New name");
            Assert.Equal(4, count);

            // Nothing changed on disk: reconcile stays quiet.
            await service.ReconcileAsync();
            Assert.Equal(4, count);

            await service.UninstallAsync(entry.Id);
            Assert.Equal(5, count);
        }
    }

    [Fact]
    public async Task Game_context_change_re_initializes_the_store()
    {
        using var temp = new TempGameRoot();
        temp.CreateMod("ModA");
        var (service, context, _) = Create(temp, configured: false);
        using (service)
        {
            await service.InitializeAsync();
            Assert.False(service.IsInitialized);

            var changed = NextChangedAsync(service);
            context.Set(temp.Installation);
            await changed;

            Assert.True(service.IsInitialized);
            Assert.Equal(new[] { "ModA" }, service.UnmanagedFolders);

            changed = NextChangedAsync(service);
            context.Set(null);
            await changed;

            Assert.False(service.IsInitialized);
            Assert.Empty(service.UnmanagedFolders);
        }
    }

    [Fact]
    public async Task Operations_without_a_game_throw_invalid_operation()
    {
        using var temp = new TempGameRoot();
        var (service, _, _) = Create(temp, configured: false);
        using (service)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.ImportUnmanagedAsync("ModA"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetEnabledAsync(Guid.NewGuid(), true));
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.UninstallAsync(Guid.NewGuid()));
        }
    }

    [Fact]
    public async Task Snapshots_are_immutable_copies()
    {
        using var temp = new TempGameRoot();
        temp.CreateMod("ModA");
        var (service, _, _) = Create(temp);
        using (service)
        {
            await service.InitializeAsync();
            var unmanagedBefore = service.UnmanagedFolders;
            var modsBefore = service.Mods;

            await ImportAsync(service, "ModA");

            Assert.Equal(new[] { "ModA" }, unmanagedBefore);
            Assert.Empty(modsBefore);
            Assert.Empty(service.UnmanagedFolders);
            Assert.Single(service.Mods);
        }
    }

    [Fact]
    public async Task Manifest_entries_survive_restart_with_their_states()
    {
        using var temp = new TempGameRoot();
        temp.CreateMod("ModA");
        temp.CreateMod("ModB");
        Guid aId;
        Guid bId;
        var (first, _, _) = Create(temp);
        using (first)
        {
            await first.InitializeAsync();
            aId = (await ImportAsync(first, "ModA")).Id;
            bId = (await ImportAsync(first, "ModB")).Id;
            await first.SetEnabledAsync(bId, false);
        }

        var (second, _, _) = Create(temp);
        using (second)
        {
            await second.InitializeAsync();

            Assert.Equal(ModState.Enabled, second.Find(aId)!.State);
            Assert.Equal(ModState.Disabled, second.Find(bId)!.State);
            Assert.Empty(second.UnmanagedFolders);
        }
    }

    [Fact]
    public void Installation_paths_match_the_spec_layout()
    {
        using var temp = new TempGameRoot();
        var install = new GameInstallation { Root = temp.Root, Source = GameSource.Steam };

        Assert.Equal(temp.ModsDirectory, install.ModsDirectory);
        Assert.Equal(temp.DisabledModsDirectory, install.DisabledModsDirectory);
        Assert.False(install.DisabledModsDirectory.StartsWith(install.PaksDirectory, StringComparison.OrdinalIgnoreCase));
    }
}
