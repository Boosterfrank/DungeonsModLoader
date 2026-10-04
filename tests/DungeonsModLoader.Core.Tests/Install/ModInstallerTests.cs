using System.IO.Compression;
using DungeonsModLoader.Core.Install;
using DungeonsModLoader.Core.Mods;
using DungeonsModLoader.Core.Tests.Mods;
using Microsoft.Extensions.Logging.Abstractions;

namespace DungeonsModLoader.Core.Tests.Install;

public class ModInstallerTests
{
    [Fact]
    public async Task Pak_only_archive_is_installed_flattened_and_recorded()
    {
        using var temp = new TempGameRoot();
        var (installer, mods) = Create(temp);
        var zip = temp.Zip("Cool Mod-5-1-2-1696000000.zip", ("CoolMod/CoolMod_P.pak", "p"), ("CoolMod/CoolMod_P.ucas", "c"), ("CoolMod/CoolMod_P.utoc", "t"), ("CoolMod/readme.txt", "hi"));

        using var plan = await installer.InspectAsync(InstallSource.FromArchive(zip));
        Assert.Equal("Cool Mod", plan.SuggestedName);
        Assert.Equal("1.2", plan.SuggestedVersion);
        Assert.False(plan.RequiresChoice);
        Assert.False(plan.PreserveStructure);
        Assert.True(Directory.Exists(plan.StagingRoot));

        var entry = await installer.InstallAsync(new InstallRequest(plan, plan.DefaultSelection, plan.SuggestedName));

        var folder = temp.EnabledPath("Cool Mod");
        Assert.Equal("Cool Mod", entry.FolderName);
        Assert.Equal("Cool Mod", entry.DisplayName);
        Assert.Equal("1.2", entry.Version);
        Assert.Equal(ModSource.Local, entry.Source);
        Assert.True(File.Exists(Path.Combine(folder, "CoolMod_P.pak")));
        Assert.True(File.Exists(Path.Combine(folder, "CoolMod_P.ucas")));
        Assert.True(File.Exists(Path.Combine(folder, "CoolMod_P.utoc")));
        Assert.True(File.Exists(Path.Combine(folder, "readme.txt")));
        Assert.Empty(Directory.GetDirectories(folder));
        Assert.Equal(4, entry.Files.Count);
        Assert.Equal(ModState.Enabled, mods.Find(entry.Id)!.State);
        Assert.Empty(Directory.GetDirectories(temp.DisabledModsDirectory));

        var staging = plan.StagingRoot;
        plan.Dispose();
        Assert.False(Directory.Exists(staging));
    }

    [Fact]
    public async Task Data_driven_archive_keeps_its_folder_layout()
    {
        using var temp = new TempGameRoot();
        var (installer, _) = Create(temp);
        var zip = temp.Zip("SkinPack.zip", ("pak/SkinPack_P.pak", "p"), ("pak/SkinPack_P.ucas", "c"), ("pak/SkinPack_P.utoc", "t"), ("skins/Steve.png", "png"), ("capes/_defaults/Hero.png", "png"), ("READ_THIS_FILE.txt", "read"));

        using var plan = await installer.InspectAsync(InstallSource.FromArchive(zip));
        Assert.True(plan.PreserveStructure);
        Assert.False(plan.RequiresChoice);

        var entry = await installer.InstallAsync(new InstallRequest(plan, plan.DefaultSelection, plan.SuggestedName));

        var folder = temp.EnabledPath("SkinPack");
        Assert.True(File.Exists(Path.Combine(folder, "pak", "SkinPack_P.pak")));
        Assert.True(File.Exists(Path.Combine(folder, "skins", "Steve.png")));
        Assert.True(File.Exists(Path.Combine(folder, "capes", "_defaults", "Hero.png")));
        Assert.True(File.Exists(Path.Combine(folder, "READ_THIS_FILE.txt")));
        Assert.Equal(6, entry.Files.Count);
        Assert.Contains(entry.Files, f => f.RelativePath == "capes/_defaults/Hero.png");
    }

    [Fact]
    public async Task Variant_folders_require_a_choice_and_only_the_chosen_set_is_installed()
    {
        using var temp = new TempGameRoot();
        var (installer, _) = Create(temp);
        var zip = temp.Zip("Variants.zip", ("Option A/VarA_P.pak", "a"), ("Option A/VarA_P.ucas", "a"), ("Option A/VarA_P.utoc", "a"), ("Option B/VarB_P.pak", "b"), ("Option B/VarB_P.ucas", "b"), ("Option B/VarB_P.utoc", "b"));

        using var plan = await installer.InspectAsync(InstallSource.FromArchive(zip));
        Assert.True(plan.RequiresChoice);
        Assert.Equal(2, plan.Candidates.Count);
        Assert.Equal("Option A", plan.Candidates[0].Name);

        var optionB = plan.Candidates[1].FileSets;
        var entry = await installer.InstallAsync(new InstallRequest(plan, optionB, "Variants"));

        var folder = temp.EnabledPath("Variants");
        Assert.True(File.Exists(Path.Combine(folder, "VarB_P.pak")));
        Assert.False(File.Exists(Path.Combine(folder, "VarA_P.pak")));
        Assert.Empty(Directory.GetDirectories(folder));
        Assert.Equal(3, entry.Files.Count);
    }

    [Fact]
    public async Task Variant_with_data_files_is_re_rooted_at_the_variant_folder()
    {
        using var temp = new TempGameRoot();
        var (installer, _) = Create(temp);
        var zip = temp.Zip("DataVariants.zip",
            ("Option A/pak/A_P.pak", "a"), ("Option A/pak/A_P.ucas", "a"), ("Option A/pak/A_P.utoc", "a"), ("Option A/skins/a.png", "a"),
            ("Option B/pak/B_P.pak", "b"), ("Option B/pak/B_P.ucas", "b"), ("Option B/pak/B_P.utoc", "b"), ("Option B/skins/b.png", "b"));

        using var plan = await installer.InspectAsync(InstallSource.FromArchive(zip));
        Assert.True(plan.PreserveStructure);
        var optionA = plan.Candidates.Single(c => c.RelativeDirectory == "Option A/pak").FileSets;

        var entry = await installer.InstallAsync(new InstallRequest(plan, optionA, "Data"));

        var folder = temp.EnabledPath("Data");
        Assert.True(File.Exists(Path.Combine(folder, "pak", "A_P.pak")));
        Assert.True(File.Exists(Path.Combine(folder, "skins", "a.png")));
        Assert.False(Directory.Exists(Path.Combine(folder, "Option A")));
        Assert.False(File.Exists(Path.Combine(folder, "skins", "b.png")));
        Assert.Equal(4, entry.Files.Count);
    }

    [Fact]
    public async Task Unmanaged_folder_conflict_asks_then_keeps_both_or_replaces()
    {
        using var temp = new TempGameRoot();
        var (installer, mods) = Create(temp);
        var stray = temp.EnabledPath("Cool Mod");
        Directory.CreateDirectory(stray);
        await File.WriteAllTextAsync(Path.Combine(stray, "old.pak"), "old");
        var zip = temp.Zip("Cool Mod.zip", ("CoolMod_P.pak", "p"), ("CoolMod_P.ucas", "c"), ("CoolMod_P.utoc", "t"));

        using var plan = await installer.InspectAsync(InstallSource.FromArchive(zip));

        var conflict = await Assert.ThrowsAsync<InstallConflictException>(() =>
            installer.InstallAsync(new InstallRequest(plan, plan.DefaultSelection, "Cool Mod")));
        Assert.Equal(InstallConflictKind.UnmanagedFolder, conflict.Kind);
        Assert.Equal("Cool Mod", conflict.FolderName);
        Assert.Null(conflict.ExistingMod);

        var copy = await installer.InstallAsync(new InstallRequest(plan, plan.DefaultSelection, "Cool Mod", ConflictResolution.KeepBoth));
        Assert.Equal("Cool Mod (2)", copy.FolderName);
        Assert.True(File.Exists(Path.Combine(stray, "old.pak")));

        var replaced = await installer.InstallAsync(new InstallRequest(plan, plan.DefaultSelection, "Cool Mod", ConflictResolution.Replace));
        Assert.Equal("Cool Mod", replaced.FolderName);
        Assert.False(File.Exists(Path.Combine(stray, "old.pak")));
        Assert.True(File.Exists(Path.Combine(stray, "CoolMod_P.pak")));
        Assert.Equal(2, mods.Mods.Count);
    }

    [Fact]
    public async Task Managed_conflict_replace_keeps_display_name_and_disabled_state()
    {
        using var temp = new TempGameRoot();
        var (installer, mods) = Create(temp);
        var zip = temp.Zip("Cool Mod.zip", ("CoolMod_P.pak", "v1"), ("CoolMod_P.ucas", "c"), ("CoolMod_P.utoc", "t"));
        using var plan = await installer.InspectAsync(InstallSource.FromArchive(zip));
        var first = await installer.InstallAsync(new InstallRequest(plan, plan.DefaultSelection, "Cool Mod"));
        await mods.RenameAsync(first.Id, "My favourite");
        await mods.SetEnabledAsync(first.Id, false);

        var zip2 = temp.Zip("Cool Mod v2.zip", ("CoolMod_P.pak", "v2"), ("CoolMod_P.ucas", "c"), ("CoolMod_P.utoc", "t"));
        using var plan2 = await installer.InspectAsync(InstallSource.FromArchive(zip2));

        var conflict = await Assert.ThrowsAsync<InstallConflictException>(() =>
            installer.InstallAsync(new InstallRequest(plan2, plan2.DefaultSelection, "Cool Mod")));
        Assert.Equal(InstallConflictKind.ManagedMod, conflict.Kind);
        Assert.Equal(first.Id, conflict.ExistingMod!.Id);

        var replaced = await installer.InstallAsync(new InstallRequest(plan2, plan2.DefaultSelection, "Cool Mod", ConflictResolution.Replace));

        var info = Assert.Single(mods.Mods);
        Assert.Equal(replaced.Id, info.Entry.Id);
        Assert.Equal("My favourite", info.Entry.DisplayName);
        Assert.Equal(ModState.Disabled, info.State);
        Assert.Equal("2", replaced.Version);
        Assert.Equal("v2", await File.ReadAllTextAsync(Path.Combine(temp.DisabledPath("Cool Mod"), "CoolMod_P.pak")));
        Assert.False(Directory.Exists(temp.EnabledPath("Cool Mod")));
    }

    [Fact]
    public async Task Loose_files_are_installed_under_the_pak_name()
    {
        using var temp = new TempGameRoot();
        var (installer, _) = Create(temp);
        var loose = Path.Combine(temp.BaseDirectory, "loose");
        Directory.CreateDirectory(loose);
        var files = new[] { "Free_Cam_P.pak", "Free_Cam_P.ucas", "Free_Cam_P.utoc" }.Select(n => Path.Combine(loose, n)).ToArray();
        foreach (var file in files)
        {
            await File.WriteAllTextAsync(file, "x");
        }

        using var plan = await installer.InspectAsync(InstallSource.FromLooseFiles(files));
        Assert.Equal("Free Cam", plan.SuggestedName);
        Assert.False(plan.RequiresChoice);

        var entry = await installer.InstallAsync(new InstallRequest(plan, plan.DefaultSelection, plan.SuggestedName));

        Assert.Equal("Free Cam", entry.FolderName);
        Assert.True(File.Exists(Path.Combine(temp.EnabledPath("Free Cam"), "Free_Cam_P.utoc")));
        Assert.True(File.Exists(files[0])); // originals untouched
    }

    [Fact]
    public async Task Folder_source_is_copied_and_left_in_place()
    {
        using var temp = new TempGameRoot();
        var (installer, _) = Create(temp);
        var folder = Path.Combine(temp.BaseDirectory, "MyMod v1.0");
        Directory.CreateDirectory(folder);
        foreach (var name in new[] { "MyMod_P.pak", "MyMod_P.ucas", "MyMod_P.utoc" })
        {
            await File.WriteAllTextAsync(Path.Combine(folder, name), "x");
        }

        using var plan = await installer.InspectAsync(InstallSource.FromFolder(folder));
        Assert.Equal("MyMod", plan.SuggestedName);
        Assert.Equal("1.0", plan.SuggestedVersion);

        var entry = await installer.InstallAsync(new InstallRequest(plan, plan.DefaultSelection, plan.SuggestedName));
        plan.Dispose();

        Assert.True(File.Exists(Path.Combine(temp.EnabledPath("MyMod"), "MyMod_P.pak")));
        Assert.True(Directory.Exists(folder));
        Assert.Equal("1.0", entry.Version);
    }

    [Fact]
    public async Task Archive_without_mod_files_is_rejected_and_leaves_no_staging()
    {
        using var temp = new TempGameRoot();
        var (installer, _) = Create(temp);
        var zip = temp.Zip("docs.zip", ("readme.txt", "hi"), ("image.png", "png"));

        var ex = await Assert.ThrowsAsync<InstallPackageException>(() => installer.InspectAsync(InstallSource.FromArchive(zip)));

        Assert.Contains("docs", ex.Message);
        Assert.Empty(Directory.GetDirectories(temp.Paths.TempDirectory));
    }

    [Fact]
    public async Task Selecting_nothing_valid_is_rejected()
    {
        using var temp = new TempGameRoot();
        var (installer, _) = Create(temp);
        var zip = temp.Zip("broken.zip", ("Broken_P.pak", "p"), ("Broken_P.ucas", "c"), ("Ok_P.pak", "p"));

        using var plan = await installer.InspectAsync(InstallSource.FromArchive(zip));
        var broken = plan.AllFileSets.Single(s => s.BaseName == "Broken_P");

        await Assert.ThrowsAsync<InstallPackageException>(() => installer.InstallAsync(new InstallRequest(plan, [broken], "Broken")));
        Assert.Empty(Directory.GetDirectories(temp.ModsDirectory));
    }

    [Fact]
    public async Task Cancelled_install_leaves_no_folders_behind()
    {
        using var temp = new TempGameRoot();
        var (installer, mods) = Create(temp);
        var zip = temp.Zip("Cool Mod.zip", ("CoolMod_P.pak", "p"), ("CoolMod_P.ucas", "c"), ("CoolMod_P.utoc", "t"));
        using var plan = await installer.InspectAsync(InstallSource.FromArchive(zip));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            installer.InstallAsync(new InstallRequest(plan, plan.DefaultSelection, "Cool Mod"), null, cts.Token));

        Assert.Empty(Directory.GetDirectories(temp.ModsDirectory));
        Assert.Empty(Directory.GetDirectories(temp.DisabledModsDirectory));
        Assert.Empty(mods.Mods);
    }

    [Fact]
    public async Task Install_folder_name_is_sanitized_from_the_display_name()
    {
        using var temp = new TempGameRoot();
        var (installer, _) = Create(temp);
        var zip = temp.Zip("x.zip", ("X_P.pak", "p"), ("X_P.ucas", "c"), ("X_P.utoc", "t"));
        using var plan = await installer.InspectAsync(InstallSource.FromArchive(zip));

        var entry = await installer.InstallAsync(new InstallRequest(plan, plan.DefaultSelection, "What: a <mod>?"));

        Assert.Equal("What a mod", entry.FolderName);
        Assert.Equal("What: a <mod>?", entry.DisplayName);
        Assert.True(Directory.Exists(temp.EnabledPath("What a mod")));
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

internal static class TempGameRootZipExtensions
{
    /// <summary>Creates a zip under the temp root's base directory with the given entries.</summary>
    public static string Zip(this TempGameRoot temp, string fileName, params (string Name, string Content)[] entries)
    {
        var path = Path.Combine(temp.BaseDirectory, fileName);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var entry = archive.CreateEntry(name);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
        }

        return path;
    }
}
