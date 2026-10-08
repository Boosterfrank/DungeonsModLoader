using DungeonsModLoader.Core.Mods;
using Microsoft.Extensions.Logging.Abstractions;

namespace DungeonsModLoader.Core.Tests.Mods;

/// <summary>Linking a local mod to its Nexus page changes the manifest entry only; the folder is untouched.</summary>
public class ModServiceLinkTests
{
    private static ModService Create(TempGameRoot temp)
    {
        var context = new FakeGameContext(temp.Installation);
        var store = new JsonManifestStore(temp.Paths, NullLogger<JsonManifestStore>.Instance);
        return new ModService(context, store, NullLogger<ModService>.Instance, ModServiceOptions.ManualImportOnly);
    }

    [Fact]
    public async Task Link_records_the_nexus_ids_and_keeps_the_files()
    {
        using var temp = new TempGameRoot();
        var folder = temp.CreateMod("BlueprintLoader");
        using var service = Create(temp);
        await service.InitializeAsync();
        var entry = await service.ImportUnmanagedAsync("BlueprintLoader");
        var filesBefore = entry.Files.Count;
        var uploaded = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

        var changed = false;
        service.Changed += (_, _) => changed = true;
        await service.LinkToNexusAsync(entry.Id, new NexusLink(2, 77, "2.3", "ewanhowell5195", "https://x/thumb.png",
            new[] { new ModRequirementRecord(5, "Other") }, uploaded));

        var linked = service.Find(entry.Id)!.Entry;
        Assert.True(changed);
        Assert.Equal(ModSource.Nexus, linked.Source);
        Assert.Equal(2, linked.NexusModId);
        Assert.Equal(77, linked.NexusFileId);
        Assert.Equal("2.3", linked.Version);
        Assert.Equal("ewanhowell5195", linked.Author);
        Assert.Equal("https://x/thumb.png", linked.ThumbnailUrl);
        Assert.Equal(uploaded, linked.UpdatedAt);
        Assert.Equal("Other", Assert.Single(linked.Requirements!).Name);
        Assert.Equal(filesBefore, linked.Files.Count);
        Assert.True(Directory.Exists(folder));

        // Persisted: a fresh service reads the link back.
        using var again = Create(temp);
        await again.InitializeAsync();
        Assert.Equal(2, again.Find(entry.Id)!.Entry.NexusModId);
    }

    [Fact]
    public async Task Link_without_a_known_file_keeps_the_local_version_text_and_unlink_reverts_to_local()
    {
        using var temp = new TempGameRoot();
        temp.CreateMod("CoolMod");
        using var service = Create(temp);
        await service.InitializeAsync();
        var entry = await service.ImportUnmanagedAsync("CoolMod");
        entry.Version = "1.2";

        await service.LinkToNexusAsync(entry.Id, new NexusLink(9));
        var linked = service.Find(entry.Id)!.Entry;
        Assert.Equal(ModSource.Nexus, linked.Source);
        Assert.Equal(9, linked.NexusModId);
        Assert.Null(linked.NexusFileId);
        Assert.Equal("1.2", linked.Version);

        await service.UnlinkFromNexusAsync(entry.Id);
        var local = service.Find(entry.Id)!.Entry;
        Assert.Equal(ModSource.Local, local.Source);
        Assert.Null(local.NexusModId);
        Assert.Null(local.Author);
        Assert.Null(local.Requirements);
        Assert.Equal("1.2", local.Version);
    }

    [Fact]
    public async Task Link_rejects_unknown_mods_and_bad_ids()
    {
        using var temp = new TempGameRoot();
        temp.CreateMod("CoolMod");
        using var service = Create(temp);
        await service.InitializeAsync();
        var entry = await service.ImportUnmanagedAsync("CoolMod");

        await Assert.ThrowsAsync<ModNotFoundException>(() => service.LinkToNexusAsync(Guid.NewGuid(), new NexusLink(1)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.LinkToNexusAsync(entry.Id, new NexusLink(0)));
        Assert.Equal(ModSource.Local, service.Find(entry.Id)!.Entry.Source);
    }
}
