using DungeonsModLoader.Core.Mods;
using Microsoft.Extensions.Logging.Abstractions;

namespace DungeonsModLoader.Core.Tests.Mods;

/// <summary><see cref="IModService.ApplyEnabledStatesAsync"/>: the bulk move behind profile switching.</summary>
public class ModServiceApplyTests
{
    private static ModService Create(TempGameRoot temp)
    {
        var context = new FakeGameContext(temp.Installation);
        var store = new JsonManifestStore(temp.Paths, NullLogger<JsonManifestStore>.Instance);
        return new ModService(context, store, NullLogger<ModService>.Instance);
    }

    private static Guid Id(ModService service, string folder) => service.Mods.Single(m => m.Entry.FolderName == folder).Entry.Id;

    [Fact]
    public async Task Apply_enables_and_disables_to_match_the_set()
    {
        using var temp = new TempGameRoot();
        temp.CreateMod("ModA");
        temp.CreateMod("ModB");
        temp.CreateMod("ModC", enabled: false);
        using var service = Create(temp);
        await service.InitializeAsync();
        var progress = new List<string>();

        await service.ApplyEnabledStatesAsync(new HashSet<Guid> { Id(service, "ModA"), Id(service, "ModC") }, new Progress<string>(progress.Add));

        Assert.Equal(ModState.Enabled, service.Find(Id(service, "ModA"))!.State);
        Assert.Equal(ModState.Disabled, service.Find(Id(service, "ModB"))!.State);
        Assert.Equal(ModState.Enabled, service.Find(Id(service, "ModC"))!.State);
        Assert.True(Directory.Exists(temp.DisabledPath("ModB")));
        Assert.True(Directory.Exists(temp.EnabledPath("ModC")));
    }

    [Fact]
    public async Task Apply_rolls_back_when_a_move_fails()
    {
        using var temp = new TempGameRoot();
        temp.CreateMod("ModA");
        var folderB = temp.CreateMod("ModB");
        using var service = Create(temp);
        await service.InitializeAsync();

        ModApplyException error;
        // ModA is disabled first (alphabetical), then ModB fails because a file inside it is open.
        await using (new FileStream(Path.Combine(folderB, "ModB_P.pak"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            error = await Assert.ThrowsAsync<ModApplyException>(() => service.ApplyEnabledStatesAsync(new HashSet<Guid>()));
        }

        Assert.Equal("ModB", error.FailedMod.FolderName);
        Assert.False(error.WasEnabling);
        Assert.True(error.RolledBack);
        Assert.Contains("put back", error.Message);
        Assert.True(Directory.Exists(temp.EnabledPath("ModA")));
        Assert.True(Directory.Exists(temp.EnabledPath("ModB")));
        Assert.Empty(Directory.GetDirectories(temp.DisabledModsDirectory));
        Assert.All(service.Mods, m => Assert.Equal(ModState.Enabled, m.State));
    }

    [Fact]
    public async Task Apply_with_no_changes_does_not_raise_changed()
    {
        using var temp = new TempGameRoot();
        temp.CreateMod("ModA");
        using var service = Create(temp);
        await service.InitializeAsync();
        var changed = 0;
        service.Changed += (_, _) => Interlocked.Increment(ref changed);

        await service.ApplyEnabledStatesAsync(new HashSet<Guid> { Id(service, "ModA") });

        Assert.Equal(0, changed);
    }

    [Fact]
    public async Task Apply_skips_missing_mods()
    {
        using var temp = new TempGameRoot();
        temp.CreateMod("ModA");
        temp.CreateMod("Gone");
        using var service = Create(temp);
        await service.InitializeAsync();
        var gone = Id(service, "Gone");
        Directory.Delete(temp.EnabledPath("Gone"), recursive: true);

        await service.ApplyEnabledStatesAsync(new HashSet<Guid> { gone });

        Assert.Equal(ModState.Disabled, service.Find(Id(service, "ModA"))!.State);
        Assert.Equal(ModState.Missing, service.Find(gone)!.State);
    }
}
