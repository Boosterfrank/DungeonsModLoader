using DungeonsModLoader.Core.Install;
using DungeonsModLoader.Core.Mods;
using DungeonsModLoader.Core.Tests.Mods;
using Microsoft.Extensions.Logging.Abstractions;

namespace DungeonsModLoader.Core.Tests.Install;

/// <summary>Replace must never destroy the old mod before the new one is ready, and sources inside the game are refused.</summary>
public class ModInstallerSafetyTests
{
    [Fact]
    public async Task Replace_keeps_the_old_mod_when_the_copy_fails()
    {
        using var temp = new TempGameRoot();
        var (installer, mods) = Create(temp);
        var v1 = temp.Zip("Cool Mod.zip", ("CoolMod_P.pak", "v1"), ("CoolMod_P.ucas", "c"), ("CoolMod_P.utoc", "t"));
        using (var plan1 = await installer.InspectAsync(InstallSource.FromArchive(v1)))
        {
            await installer.InstallAsync(new InstallRequest(plan1, plan1.DefaultSelection, "Cool Mod"));
        }

        var v2 = temp.Zip("Cool Mod v2.zip", ("CoolMod_P.pak", "v2"), ("CoolMod_P.ucas", "c"), ("CoolMod_P.utoc", "t"));
        using var plan2 = await installer.InspectAsync(InstallSource.FromArchive(v2));

        // Sabotage the staged content after inspection: the copy must fail before anything is deleted.
        File.Delete(Path.Combine(plan2.StagingRoot, "CoolMod_P.ucas"));

        await Assert.ThrowsAnyAsync<IOException>(() =>
            installer.InstallAsync(new InstallRequest(plan2, plan2.DefaultSelection, "Cool Mod", ConflictResolution.Replace)));

        var info = Assert.Single(mods.Mods);
        Assert.Equal(ModState.Enabled, info.State);
        Assert.Equal("v1", await File.ReadAllTextAsync(Path.Combine(temp.EnabledPath("Cool Mod"), "CoolMod_P.pak")));
        Assert.Empty(Directory.GetDirectories(temp.DisabledModsDirectory));
    }

    [Fact]
    public async Task Replace_of_an_unmanaged_folder_happens_only_after_the_new_folder_is_built()
    {
        using var temp = new TempGameRoot();
        var (installer, mods) = Create(temp);
        var stray = temp.EnabledPath("Cool Mod");
        Directory.CreateDirectory(stray);
        await File.WriteAllTextAsync(Path.Combine(stray, "keep.pak"), "old");
        var zip = temp.Zip("Cool Mod.zip", ("CoolMod_P.pak", "p"), ("CoolMod_P.ucas", "c"), ("CoolMod_P.utoc", "t"));
        using var plan = await installer.InspectAsync(InstallSource.FromArchive(zip));
        File.Delete(Path.Combine(plan.StagingRoot, "CoolMod_P.utoc"));

        await Assert.ThrowsAnyAsync<IOException>(() =>
            installer.InstallAsync(new InstallRequest(plan, plan.DefaultSelection, "Cool Mod", ConflictResolution.Replace)));

        Assert.True(File.Exists(Path.Combine(stray, "keep.pak")));
        Assert.Empty(mods.Mods);
    }

    [Fact]
    public async Task Folder_sources_inside_the_game_installation_are_refused()
    {
        using var temp = new TempGameRoot();
        var (installer, _) = Create(temp);
        var inside = temp.CreateMod("Existing");

        var ex = await Assert.ThrowsAsync<InstallPackageException>(() => installer.InspectAsync(InstallSource.FromFolder(inside)));
        Assert.Contains("Import", ex.Message);

        await Assert.ThrowsAsync<InstallPackageException>(() => installer.InspectAsync(InstallSource.FromFolder(temp.Root)));
        await Assert.ThrowsAsync<InstallPackageException>(() => installer.InspectAsync(InstallSource.FromFolder(temp.BaseDirectory)));
        Assert.True(Directory.Exists(inside));
    }

    [Fact]
    public async Task Selecting_two_variants_with_the_same_file_names_is_refused_when_flattening()
    {
        using var temp = new TempGameRoot();
        var (installer, _) = Create(temp);
        var zip = temp.Zip("Same.zip", ("Option A/Mod_P.pak", "a"), ("Option A/Mod_P.ucas", "a"), ("Option A/Mod_P.utoc", "a"), ("Option B/Mod_P.pak", "b"), ("Option B/Mod_P.ucas", "b"), ("Option B/Mod_P.utoc", "b"));
        using var plan = await installer.InspectAsync(InstallSource.FromArchive(zip));

        var ex = await Assert.ThrowsAsync<InstallPackageException>(() =>
            installer.InstallAsync(new InstallRequest(plan, plan.AllFileSets, "Same")));

        Assert.Contains("Mod_P", ex.Message);
        Assert.Empty(Directory.GetDirectories(temp.ModsDirectory));
    }

    [Fact]
    public async Task Inspection_honours_cancellation_while_scanning()
    {
        using var temp = new TempGameRoot();
        var (installer, _) = Create(temp);
        var folder = Path.Combine(temp.BaseDirectory, "big");
        for (var i = 0; i < 30; i++)
        {
            Directory.CreateDirectory(Path.Combine(folder, "d" + i));
            await File.WriteAllTextAsync(Path.Combine(folder, "d" + i, "x_P.pak"), "x");
        }

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => installer.InspectAsync(InstallSource.FromFolder(folder), null, cts.Token));
    }

    private static (ModInstaller Installer, ModService Mods) Create(TempGameRoot temp)
    {
        var context = new FakeGameContext(temp.Installation);
        var store = new JsonManifestStore(temp.Paths, NullLogger<JsonManifestStore>.Instance);
        var mods = new ModService(context, store, NullLogger<ModService>.Instance);
        mods.InitializeAsync().GetAwaiter().GetResult();
        var installer = new ModInstaller(context, mods, temp.Paths, NullLogger<ModInstaller>.Instance);
        return (installer, mods);
    }
}
