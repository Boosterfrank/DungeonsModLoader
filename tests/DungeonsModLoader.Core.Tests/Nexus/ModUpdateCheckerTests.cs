using DungeonsModLoader.Core.Mods;
using DungeonsModLoader.Core.Settings;
using DungeonsModLoader.Core.Tests.Mods;
using DungeonsModLoader.Nexus.Api;
using DungeonsModLoader.Nexus.Auth;
using DungeonsModLoader.Nexus.Updates;
using Microsoft.Extensions.Logging.Abstractions;

namespace DungeonsModLoader.Core.Tests.Nexus;

public class ModUpdateCheckerTests
{
    private sealed class FakeSession : INexusSession
    {
        public NexusSessionStatus Status => NexusSessionStatus.LoggedIn;
        public NexusUser? User => new(1, "T", true, false, null, null);
        public bool HasApiKey { get; set; } = true;
        public bool IsPremium => true;
        public string? LastError => null;
        public IReadOnlyList<INexusAuthProvider> Providers => Array.Empty<INexusAuthProvider>();
        public event EventHandler? Changed { add { } remove { } }
        public Task RestoreAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<NexusUser> LoginAsync(INexusAuthProvider provider, string? userInput = null, IProgress<string>? progress = null, CancellationToken cancellationToken = default) => Task.FromResult(User!);
        public Task<NexusUser?> RevalidateAsync(CancellationToken cancellationToken = default) => Task.FromResult(User);
        public Task LogoutAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static NexusFile File(long id, NexusFileCategory category, int day, string version) =>
        new(id, "Cool Mod", version, category, category == NexusFileCategory.Main, 100, $"Cool Mod-5-{version}.zip", new DateTimeOffset(2026, 1, day, 0, 0, 0, TimeSpan.Zero), version, null, null);

    private static async Task<(ModUpdateChecker Checker, ModService Mods, FakeNexusClient Client, ModEntry Entry)> CreateAsync(TempGameRoot temp, long? installedFileId = 10)
    {
        var context = new FakeGameContext(temp.Installation);
        var store = new JsonManifestStore(temp.Paths, NullLogger<JsonManifestStore>.Instance);
        var mods = new ModService(context, store, NullLogger<ModService>.Instance, ModServiceOptions.ManualImportOnly);
        await mods.InitializeAsync();
        temp.CreateMod("Cool Mod");
        var entry = new ModEntry
        {
            FolderName = "Cool Mod",
            DisplayName = "Cool Mod",
            Source = ModSource.Nexus,
            NexusModId = 5,
            NexusFileId = installedFileId,
            Version = "1.0",
            UpdatedAt = DateTimeOffset.UtcNow.AddDays(-2),
        };
        await mods.AddInstalledAsync(entry);
        var settings = new JsonSettingsStore(temp.Paths, NullLogger<JsonSettingsStore>.Instance);
        await settings.LoadAsync();
        var client = new FakeNexusClient();
        var checker = new ModUpdateChecker(client, new FakeSession(), mods, settings, NullLogger<ModUpdateChecker>.Instance);
        return (checker, mods, client, entry);
    }

    [Fact]
    public async Task Finds_a_newer_main_file_through_the_update_chain()
    {
        using var temp = new TempGameRoot();
        var (checker, _, client, entry) = await CreateAsync(temp);
        client.OnUpdated = () => new[] { new NexusUpdatedMod(5, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow) };
        client.OnFiles = _ => new NexusFileList(
            new[] { File(10, NexusFileCategory.OldVersion, 1, "1.0"), File(11, NexusFileCategory.Main, 5, "1.2") },
            new[] { new NexusFileUpdate(10, 11, null, null, DateTimeOffset.UtcNow) });
        var changed = 0;
        checker.Changed += (_, _) => changed++;

        var found = await checker.CheckAsync(force: true);

        var update = Assert.Single(found);
        Assert.Equal(entry.Id, update.ModId);
        Assert.Equal(11, update.NewFile.FileId);
        Assert.Equal("1.2", update.NewVersion);
        Assert.True(checker.Updates.ContainsKey(entry.Id));
        Assert.NotNull(checker.LastCheckUtc);
        Assert.True(changed >= 1);
    }

    [Fact]
    public async Task Mods_not_in_the_recent_list_skip_the_file_request()
    {
        using var temp = new TempGameRoot();
        var (checker, _, client, _) = await CreateAsync(temp);
        client.OnUpdated = () => Array.Empty<NexusUpdatedMod>();

        var found = await checker.CheckAsync(force: false);

        Assert.Empty(found);
        Assert.Equal(1, client.UpdatedCalls);
        Assert.Equal(0, client.FilesCalls);
    }

    [Fact]
    public async Task Check_is_throttled_to_once_an_hour_unless_forced()
    {
        using var temp = new TempGameRoot();
        var (checker, _, client, _) = await CreateAsync(temp);
        client.OnUpdated = () => Array.Empty<NexusUpdatedMod>();

        await checker.CheckAsync();
        await checker.CheckAsync();
        Assert.Equal(1, client.UpdatedCalls);

        await checker.CheckAsync(force: true);
        Assert.Equal(2, client.UpdatedCalls);
    }

    [Fact]
    public async Task Installing_the_suggested_file_clears_the_update()
    {
        using var temp = new TempGameRoot();
        var (checker, mods, client, entry) = await CreateAsync(temp);
        client.OnFiles = _ => new NexusFileList(new[] { File(10, NexusFileCategory.Main, 1, "1.0"), File(11, NexusFileCategory.Main, 5, "1.2") }, Array.Empty<NexusFileUpdate>());

        var update = await checker.CheckModAsync(entry.Id);
        Assert.Equal(11, update!.NewFile.FileId);

        var updated = new ModEntry
        {
            Id = entry.Id,
            FolderName = entry.FolderName,
            DisplayName = entry.DisplayName,
            Source = ModSource.Nexus,
            NexusModId = 5,
            NexusFileId = 11,
            Version = "1.2",
        };
        await mods.ReplaceInstalledAsync(updated);

        Assert.False(checker.Updates.ContainsKey(entry.Id));
    }

    [Fact]
    public async Task Check_without_a_key_does_nothing()
    {
        using var temp = new TempGameRoot();
        var context = new FakeGameContext(temp.Installation);
        var store = new JsonManifestStore(temp.Paths, NullLogger<JsonManifestStore>.Instance);
        using var mods = new ModService(context, store, NullLogger<ModService>.Instance);
        var settings = new JsonSettingsStore(temp.Paths, NullLogger<JsonSettingsStore>.Instance);
        var client = new FakeNexusClient();
        using var checker = new ModUpdateChecker(client, new FakeSession { HasApiKey = false }, mods, settings, NullLogger<ModUpdateChecker>.Instance);

        Assert.Empty(await checker.CheckAsync(force: true));
        Assert.Equal(0, client.UpdatedCalls);
    }
}
