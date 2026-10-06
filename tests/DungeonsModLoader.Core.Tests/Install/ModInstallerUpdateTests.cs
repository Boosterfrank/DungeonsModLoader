using DungeonsModLoader.Core.Install;
using DungeonsModLoader.Core.Mods;
using DungeonsModLoader.Core.Tests.Mods;
using Microsoft.Extensions.Logging.Abstractions;

namespace DungeonsModLoader.Core.Tests.Install;

/// <summary>In-place updates: same id, folder, display name and enabled state; backups kept; failures restored.</summary>
public class ModInstallerUpdateTests
{
    private static (ModInstaller Installer, ModService Mods) Create(TempGameRoot temp)
    {
        var context = new FakeGameContext(temp.Installation);
        var store = new JsonManifestStore(temp.Paths, NullLogger<JsonManifestStore>.Instance);
        var mods = new ModService(context, store, NullLogger<ModService>.Instance);
        mods.InitializeAsync().GetAwaiter().GetResult();
        var installer = new ModInstaller(context, mods, temp.Paths, NullLogger<ModInstaller>.Instance);
        return (installer, mods);
    }

    private static readonly ModMetadata V1 = new(ModSource.Nexus, NexusModId: 5, NexusFileId: 10, Version: "1.0", Author: "Alice", ThumbnailUrl: "https://t/1.png");
    private static readonly ModMetadata V2 = new(ModSource.Nexus, NexusModId: 5, NexusFileId: 11, Version: "1.2", Author: "Alice", ThumbnailUrl: "https://t/2.png");

    [Fact]
    public async Task Install_records_nexus_metadata()
    {
        using var temp = new TempGameRoot();
        var (installer, mods) = Create(temp);
        var zip = temp.Zip("Cool Mod-5-1-0.zip", ("CoolMod_P.pak", "v1"), ("CoolMod_P.ucas", "c"), ("CoolMod_P.utoc", "t"));
        using var plan = await installer.InspectAsync(InstallSource.FromArchive(zip));

        var entry = await installer.InstallAsync(new InstallRequest(plan, plan.DefaultSelection, "Cool Mod", Metadata: V1));

        Assert.Equal(ModSource.Nexus, entry.Source);
        Assert.Equal(5, entry.NexusModId);
        Assert.Equal(10, entry.NexusFileId);
        Assert.Equal("1.0", entry.Version);
        Assert.Equal("Alice", entry.Author);
        Assert.Equal("https://t/1.png", entry.ThumbnailUrl);
        Assert.Equal(ModSource.Nexus, mods.Find(entry.Id)!.Entry.Source);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Update_keeps_id_name_and_state_and_backs_up_the_old_folder(bool enabled)
    {
        using var temp = new TempGameRoot();
        var (installer, mods) = Create(temp);
        var zip1 = temp.Zip("Cool Mod-5-1-0.zip", ("CoolMod_P.pak", "v1"), ("CoolMod_P.ucas", "c"), ("CoolMod_P.utoc", "t"));
        ModEntry first;
        using (var plan1 = await installer.InspectAsync(InstallSource.FromArchive(zip1)))
        {
            first = await installer.InstallAsync(new InstallRequest(plan1, plan1.DefaultSelection, "Cool Mod", Metadata: V1));
        }

        await mods.RenameAsync(first.Id, "My favourite");
        if (!enabled)
        {
            await mods.SetEnabledAsync(first.Id, false);
        }

        var zip2 = temp.Zip("Cool Mod-5-1-2.zip", ("CoolMod_P.pak", "v2"), ("CoolMod_P.ucas", "c"), ("CoolMod_P.utoc", "t"), ("readme.txt", "new"));
        using var plan2 = await installer.InspectAsync(InstallSource.FromArchive(zip2));

        var updated = await installer.InstallAsync(new InstallRequest(plan2, plan2.DefaultSelection, "ignored", Metadata: V2, UpdateOf: first.Id));

        Assert.Equal(first.Id, updated.Id);
        Assert.Equal("Cool Mod", updated.FolderName);
        Assert.Equal("My favourite", updated.DisplayName);
        Assert.Equal(11, updated.NexusFileId);
        Assert.Equal("1.2", updated.Version);
        Assert.Equal(first.InstalledAt, updated.InstalledAt);
        Assert.True(updated.UpdatedAt > first.UpdatedAt);
        Assert.Equal(4, updated.Files.Count);

        var info = Assert.Single(mods.Mods);
        Assert.Equal(enabled ? ModState.Enabled : ModState.Disabled, info.State);
        var folder = enabled ? temp.EnabledPath("Cool Mod") : temp.DisabledPath("Cool Mod");
        Assert.Equal("v2", await File.ReadAllTextAsync(Path.Combine(folder, "CoolMod_P.pak")));
        Assert.True(File.Exists(Path.Combine(folder, "readme.txt")));

        var backups = Directory.GetDirectories(Path.Combine(temp.Paths.BackupsDirectory, "Cool Mod"));
        var backup = Assert.Single(backups);
        Assert.EndsWith("_1.0", backup);
        Assert.Equal("v1", await File.ReadAllTextAsync(Path.Combine(backup, "CoolMod_P.pak")));
        Assert.Empty(Directory.GetDirectories(temp.DisabledModsDirectory).Where(d => Path.GetFileName(d).StartsWith(".dml-", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Only_the_last_two_backups_are_kept()
    {
        using var temp = new TempGameRoot();
        var (installer, _) = Create(temp);
        var zip = temp.Zip("Cool Mod.zip", ("CoolMod_P.pak", "v1"), ("CoolMod_P.ucas", "c"), ("CoolMod_P.utoc", "t"));
        ModEntry entry;
        using (var plan = await installer.InspectAsync(InstallSource.FromArchive(zip)))
        {
            entry = await installer.InstallAsync(new InstallRequest(plan, plan.DefaultSelection, "Cool Mod", Metadata: V1));
        }

        for (var i = 2; i <= 4; i++)
        {
            var next = temp.Zip($"Cool Mod v{i}.zip", ("CoolMod_P.pak", "v" + i), ("CoolMod_P.ucas", "c"), ("CoolMod_P.utoc", "t"));
            using var plan = await installer.InspectAsync(InstallSource.FromArchive(next));
            await installer.InstallAsync(new InstallRequest(plan, plan.DefaultSelection, "x", Metadata: V1 with { Version = i.ToString() }, UpdateOf: entry.Id));
            await Task.Delay(1100); // distinct timestamps in the backup names
        }

        Assert.Equal(ModInstaller.BackupsToKeep, Directory.GetDirectories(Path.Combine(temp.Paths.BackupsDirectory, "Cool Mod")).Length);
    }

    [Fact]
    public async Task Failed_update_restores_the_previous_version()
    {
        using var temp = new TempGameRoot();
        var (installer, mods) = Create(temp);
        var zip1 = temp.Zip("Cool Mod.zip", ("CoolMod_P.pak", "v1"), ("CoolMod_P.ucas", "c"), ("CoolMod_P.utoc", "t"));
        ModEntry first;
        using (var plan1 = await installer.InspectAsync(InstallSource.FromArchive(zip1)))
        {
            first = await installer.InstallAsync(new InstallRequest(plan1, plan1.DefaultSelection, "Cool Mod", Metadata: V1));
        }

        var zip2 = temp.Zip("Cool Mod v2.zip", ("CoolMod_P.pak", "v2"), ("CoolMod_P.ucas", "c"), ("CoolMod_P.utoc", "t"));
        using var plan2 = await installer.InspectAsync(InstallSource.FromArchive(zip2));

        // Make the manifest unwritable so recording the update fails after the folders were swapped.
        File.Delete(temp.Paths.ManifestFile);
        Directory.CreateDirectory(temp.Paths.ManifestFile);
        try
        {
            await Assert.ThrowsAsync<ModOperationException>(() =>
                installer.InstallAsync(new InstallRequest(plan2, plan2.DefaultSelection, "x", Metadata: V2, UpdateOf: first.Id)));
        }
        finally
        {
            Directory.Delete(temp.Paths.ManifestFile);
        }

        Assert.Equal("v1", await File.ReadAllTextAsync(Path.Combine(temp.EnabledPath("Cool Mod"), "CoolMod_P.pak")));
        Assert.Equal(10, mods.Find(first.Id)!.Entry.NexusFileId);
        Assert.Empty(Directory.Exists(Path.Combine(temp.Paths.BackupsDirectory, "Cool Mod")) ? Directory.GetDirectories(Path.Combine(temp.Paths.BackupsDirectory, "Cool Mod")) : Array.Empty<string>());
    }

    [Fact]
    public async Task Update_of_a_removed_mod_is_refused()
    {
        using var temp = new TempGameRoot();
        var (installer, _) = Create(temp);
        var zip = temp.Zip("Cool Mod.zip", ("CoolMod_P.pak", "v1"), ("CoolMod_P.ucas", "c"), ("CoolMod_P.utoc", "t"));
        using var plan = await installer.InspectAsync(InstallSource.FromArchive(zip));

        await Assert.ThrowsAsync<ModNotFoundException>(() =>
            installer.InstallAsync(new InstallRequest(plan, plan.DefaultSelection, "x", UpdateOf: Guid.NewGuid())));
    }
}
