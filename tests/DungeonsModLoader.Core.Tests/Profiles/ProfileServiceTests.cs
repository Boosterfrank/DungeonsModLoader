using DungeonsModLoader.Core.Mods;
using DungeonsModLoader.Core.Profiles;
using DungeonsModLoader.Core.Settings;
using DungeonsModLoader.Core.Storage;
using DungeonsModLoader.Core.Tests.Mods;
using Microsoft.Extensions.Logging.Abstractions;

namespace DungeonsModLoader.Core.Tests.Profiles;

public class ProfileServiceTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private sealed class Fixture : IAsyncDisposable
    {
        public Fixture(TempGameRoot temp)
        {
            Temp = temp;
            var context = new FakeGameContext(temp.Installation);
            var store = new JsonManifestStore(temp.Paths, NullLogger<JsonManifestStore>.Instance);
            Mods = new ModService(context, store, NullLogger<ModService>.Instance);
            Settings = new JsonSettingsStore(temp.Paths, NullLogger<JsonSettingsStore>.Instance);
            Profiles = new ProfileService(temp.Paths, Mods, Settings, NullLogger<ProfileService>.Instance);
        }

        public TempGameRoot Temp { get; }
        public ModService Mods { get; }
        public JsonSettingsStore Settings { get; }
        public ProfileService Profiles { get; }

        public async Task StartAsync()
        {
            await Settings.LoadAsync();
            await Mods.InitializeAsync();
            await Profiles.InitializeAsync();
        }

        public Guid Id(string folder) => Mods.Mods.Single(m => m.Entry.FolderName == folder).Entry.Id;

        public ValueTask DisposeAsync()
        {
            Profiles.Dispose();
            Mods.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The condition was not met in time.");
            }

            await Task.Delay(25);
        }
    }

    [Fact]
    public async Task Initialize_creates_default_profile_from_the_enabled_mods()
    {
        using var temp = new TempGameRoot();
        temp.CreateMod("ModA");
        temp.CreateMod("ModB", enabled: false);
        await using var f = new Fixture(temp);

        await f.StartAsync();

        var profile = Assert.Single(f.Profiles.Profiles);
        Assert.Equal("Default", profile.Name);
        Assert.True(profile.IsActive);
        Assert.Equal("Default", f.Profiles.ActiveProfileName);
        Assert.Equal("ModA", Assert.Single(profile.Mods).FolderName);
        Assert.Equal(f.Id("ModA"), profile.Mods[0].ModId);
        Assert.True(File.Exists(Path.Combine(temp.Paths.ProfilesDirectory, "Default.json")));
    }

    [Fact]
    public async Task Toggling_a_mod_updates_the_active_profile()
    {
        using var temp = new TempGameRoot();
        temp.CreateMod("ModA");
        temp.CreateMod("ModB", enabled: false);
        await using var f = new Fixture(temp);
        await f.StartAsync();

        await f.Mods.SetEnabledAsync(f.Id("ModB"), true);
        await WaitUntilAsync(() => f.Profiles.Find("Default")!.ModCount == 2);

        var saved = await AtomicJsonFile.ReadAsync<Profile>(Path.Combine(temp.Paths.ProfilesDirectory, "Default.json"));
        Assert.Equal(new[] { "ModA", "ModB" }, saved!.Mods.Select(m => m.FolderName));

        await f.Mods.SetEnabledAsync(f.Id("ModA"), false);
        await WaitUntilAsync(() => f.Profiles.Find("Default")!.ModCount == 1);
        Assert.Equal("ModB", f.Profiles.Find("Default")!.Mods[0].FolderName);
    }

    [Fact]
    public async Task Create_duplicate_rename_and_delete_follow_the_rules()
    {
        using var temp = new TempGameRoot();
        temp.CreateMod("ModA");
        await using var f = new Fixture(temp);
        await f.StartAsync();

        var vanilla = await f.Profiles.CreateAsync("  Vanilla  ");
        Assert.Equal("Vanilla", vanilla.Name);
        Assert.False(vanilla.IsActive);
        Assert.Equal(1, vanilla.ModCount);

        var copy = await f.Profiles.DuplicateAsync("vanilla", "Copy");
        Assert.Equal(1, copy.ModCount);
        Assert.Equal(new[] { "Default", "Copy", "Vanilla" }, f.Profiles.Profiles.Select(p => p.Name));

        await f.Profiles.RenameAsync("Copy", "Renamed");
        Assert.Null(f.Profiles.Find("Copy"));
        Assert.NotNull(f.Profiles.Find("Renamed"));
        Assert.True(File.Exists(Path.Combine(temp.Paths.ProfilesDirectory, "Renamed.json")));
        Assert.False(File.Exists(Path.Combine(temp.Paths.ProfilesDirectory, "Copy.json")));

        await f.Profiles.DeleteAsync("Renamed");
        Assert.Null(f.Profiles.Find("Renamed"));

        await Assert.ThrowsAsync<ProfileException>(() => f.Profiles.DeleteAsync("Default"));
        await Assert.ThrowsAsync<ProfileException>(() => f.Profiles.CreateAsync("default"));
        await Assert.ThrowsAsync<ProfileException>(() => f.Profiles.CreateAsync("   "));
        await Assert.ThrowsAsync<ProfileException>(() => f.Profiles.RenameAsync("Vanilla", "Default"));
        await Assert.ThrowsAsync<ProfileException>(() => f.Profiles.SwitchAsync("Nope"));
    }

    [Fact]
    public async Task Renaming_the_active_profile_updates_the_settings()
    {
        using var temp = new TempGameRoot();
        await using var f = new Fixture(temp);
        await f.StartAsync();

        await f.Profiles.RenameAsync("Default", "Main");

        Assert.Equal("Main", f.Profiles.ActiveProfileName);
        Assert.Equal("Main", f.Settings.Current.ActiveProfile);
        var reloaded = new JsonSettingsStore(temp.Paths, NullLogger<JsonSettingsStore>.Instance);
        await reloaded.LoadAsync();
        Assert.Equal("Main", reloaded.Current.ActiveProfile);
    }

    [Fact]
    public async Task Switch_applies_the_profile_and_makes_it_active()
    {
        using var temp = new TempGameRoot();
        temp.CreateMod("ModA");
        temp.CreateMod("ModB");
        await using var f = new Fixture(temp);
        await f.StartAsync();

        await f.Mods.SetEnabledAsync(f.Id("ModB"), false);
        var onlyA = await f.Profiles.CreateAsync("Only A");
        Assert.Equal(new[] { "ModA" }, onlyA.Mods.Select(m => m.FolderName));
        await f.Mods.SetEnabledAsync(f.Id("ModB"), true);
        await WaitUntilAsync(() => f.Profiles.Find("Default")!.ModCount == 2);

        var progress = new List<string>();
        await f.Profiles.SwitchAsync("Only A", new Progress<string>(progress.Add));

        Assert.Equal("Only A", f.Profiles.ActiveProfileName);
        Assert.Equal("Only A", f.Settings.Current.ActiveProfile);
        Assert.True(f.Profiles.Find("Only A")!.IsActive);
        Assert.False(f.Profiles.Find("Default")!.IsActive);
        Assert.Equal(ModState.Disabled, f.Mods.Find(f.Id("ModB"))!.State);
        Assert.Equal(ModState.Enabled, f.Mods.Find(f.Id("ModA"))!.State);
        Assert.Equal(2, f.Profiles.Find("Default")!.ModCount);

        await f.Profiles.SwitchAsync("Default");

        Assert.Equal("Default", f.Profiles.ActiveProfileName);
        Assert.Equal(ModState.Enabled, f.Mods.Find(f.Id("ModB"))!.State);
    }

    [Fact]
    public async Task Failed_switch_keeps_the_active_profile_and_restores_folders()
    {
        using var temp = new TempGameRoot();
        temp.CreateMod("ModA");
        var folderB = temp.CreateMod("ModB");
        await using var f = new Fixture(temp);
        await f.StartAsync();
        await f.Mods.SetEnabledAsync(f.Id("ModB"), false);
        await f.Mods.SetEnabledAsync(f.Id("ModA"), false);
        await f.Profiles.CreateAsync("Nothing");
        await f.Mods.SetAllEnabledAsync(true);
        await WaitUntilAsync(() => f.Profiles.Find("Default")!.ModCount == 2);

        await using (new FileStream(Path.Combine(folderB, "ModB_P.pak"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var error = await Assert.ThrowsAsync<ModApplyException>(() => f.Profiles.SwitchAsync("Nothing"));
            Assert.Equal("ModB", error.FailedMod.FolderName);
            Assert.True(error.RolledBack);
        }

        Assert.Equal("Default", f.Profiles.ActiveProfileName);
        Assert.False(f.Profiles.Find("Nothing")!.IsActive);
        Assert.True(Directory.Exists(temp.EnabledPath("ModA")));
        Assert.True(Directory.Exists(temp.EnabledPath("ModB")));
        Assert.Equal(2, f.Profiles.Find("Default")!.ModCount);
    }

    [Fact]
    public async Task Export_and_import_round_trip_and_report_missing_mods()
    {
        using var temp = new TempGameRoot();
        temp.CreateMod("ModA");
        await using var f = new Fixture(temp);
        await f.StartAsync();
        var file = Path.Combine(temp.BaseDirectory, "shared.json");

        await f.Profiles.ExportAsync("Default", file);
        var document = await AtomicJsonFile.ReadAsync<ProfileExportDocument>(file);
        Assert.Equal(ProfileExportDocument.FormatName, document!.Format);
        Assert.Null(Assert.Single(document.Mods).ModId);

        // Pretend the file came from another PC that also had a Nexus mod we do not have.
        document.Mods.Add(new ProfileMod { FolderName = "Shiny Armor", DisplayName = "Shiny Armor", Source = ModSource.Nexus, NexusModId = 42, NexusFileId = 7 });
        await AtomicJsonFile.WriteAsync(file, document);

        var result = await f.Profiles.ImportAsync(file);

        Assert.Equal("Default (2)", result.Profile.Name);
        Assert.False(result.Profile.IsActive);
        Assert.Equal(2, result.Profile.ModCount);
        var missing = Assert.Single(result.MissingMods);
        Assert.Equal(42, missing.NexusModId);
        Assert.Equal(new[] { "Shiny Armor" }, f.Profiles.FindMissingMods("Default (2)").Select(m => m.DisplayName));

        // Switching to the imported profile resolves ModA by folder name and ignores the missing mod.
        await f.Profiles.SwitchAsync("Default (2)");
        Assert.Equal(ModState.Enabled, f.Mods.Find(f.Id("ModA"))!.State);
        Assert.Equal(f.Id("ModA"), f.Profiles.Find("Default (2)")!.Mods.Single().ModId);
    }

    [Fact]
    public async Task Import_refuses_files_that_are_not_profiles()
    {
        using var temp = new TempGameRoot();
        await using var f = new Fixture(temp);
        await f.StartAsync();
        var junk = Path.Combine(temp.BaseDirectory, "junk.json");
        await File.WriteAllTextAsync(junk, "{ \"hello\": 1 }");
        var broken = Path.Combine(temp.BaseDirectory, "broken.json");
        await File.WriteAllTextAsync(broken, "not json");

        await Assert.ThrowsAsync<ProfileException>(() => f.Profiles.ImportAsync(junk));
        await Assert.ThrowsAsync<ProfileException>(() => f.Profiles.ImportAsync(broken));
        Assert.Single(f.Profiles.Profiles);
    }

    [Fact]
    public async Task Profiles_survive_a_restart_and_the_active_one_follows_the_settings()
    {
        using var temp = new TempGameRoot();
        temp.CreateMod("ModA");
        await using (var first = new Fixture(temp))
        {
            await first.StartAsync();
            await first.Profiles.CreateAsync("Second");
            await first.Profiles.SwitchAsync("Second");
        }

        await using var second = new Fixture(temp);
        await second.StartAsync();

        Assert.Equal(new[] { "Default", "Second" }, second.Profiles.Profiles.Select(p => p.Name));
        Assert.Equal("Second", second.Profiles.ActiveProfileName);
    }
}
