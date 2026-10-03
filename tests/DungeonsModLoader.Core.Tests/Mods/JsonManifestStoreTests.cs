using System.Text.Json;
using DungeonsModLoader.Core.Mods;
using Microsoft.Extensions.Logging.Abstractions;

namespace DungeonsModLoader.Core.Tests.Mods;

public class JsonManifestStoreTests
{
    private static JsonManifestStore CreateStore(TempGameRoot temp) => new(temp.Paths, NullLogger<JsonManifestStore>.Instance);

    [Fact]
    public async Task Load_returns_empty_manifest_when_file_is_missing()
    {
        using var temp = new TempGameRoot();
        var store = CreateStore(temp);

        var manifest = await store.LoadAsync();

        Assert.NotNull(manifest);
        Assert.Empty(manifest.Mods);
        Assert.False(File.Exists(temp.Paths.ManifestFile));
    }

    [Fact]
    public async Task Save_then_load_round_trips_every_field()
    {
        using var temp = new TempGameRoot();
        var store = CreateStore(temp);
        var installedAt = new DateTimeOffset(2026, 9, 1, 12, 30, 0, TimeSpan.Zero);
        var entry = new ModEntry
        {
            Id = Guid.NewGuid(),
            FolderName = "FreeCam",
            DisplayName = "Free Camera",
            Source = ModSource.Nexus,
            NexusModId = 42,
            NexusFileId = 1234,
            Version = "1.2.0",
            Author = "someone",
            ThumbnailUrl = "https://example.invalid/thumb.png",
            InstalledAt = installedAt,
            UpdatedAt = installedAt.AddMinutes(5),
            Files =
            {
                new ModFileRecord("FreeCam_P.pak", "aa", 10),
                new ModFileRecord("data/readme.txt", "bb", 20),
            },
        };
        var manifest = new Manifest { Mods = { entry } };

        await store.SaveAsync(manifest);
        var loaded = await store.LoadAsync();

        var round = Assert.Single(loaded.Mods);
        Assert.Equal(entry.Id, round.Id);
        Assert.Equal("FreeCam", round.FolderName);
        Assert.Equal("Free Camera", round.DisplayName);
        Assert.Equal(ModSource.Nexus, round.Source);
        Assert.Equal(42, round.NexusModId);
        Assert.Equal(1234, round.NexusFileId);
        Assert.Equal("1.2.0", round.Version);
        Assert.Equal("someone", round.Author);
        Assert.Equal("https://example.invalid/thumb.png", round.ThumbnailUrl);
        Assert.Equal(installedAt, round.InstalledAt);
        Assert.Equal(installedAt.AddMinutes(5), round.UpdatedAt);
        Assert.Equal(entry.Files, round.Files);
    }

    [Fact]
    public async Task Json_uses_camel_case_lower_case_source_and_files_shape()
    {
        using var temp = new TempGameRoot();
        var store = CreateStore(temp);
        var manifest = new Manifest
        {
            Mods =
            {
                new ModEntry
                {
                    FolderName = "A",
                    DisplayName = "Mod A",
                    Source = ModSource.Local,
                    Files = { new ModFileRecord("A_P.pak", "00ff", 3) },
                },
                new ModEntry { FolderName = "B", DisplayName = "Mod B", Source = ModSource.Nexus, NexusModId = 7, NexusFileId = 8 },
            },
        };

        await store.SaveAsync(manifest);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(temp.Paths.ManifestFile));

        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        var mods = root.GetProperty("mods");
        var a = mods[0];
        foreach (var name in new[] { "id", "folderName", "displayName", "source", "installedAt", "updatedAt", "files" })
        {
            Assert.True(a.TryGetProperty(name, out _), $"missing property '{name}'");
        }

        Assert.Equal("local", a.GetProperty("source").GetString());
        Assert.Equal("nexus", mods[1].GetProperty("source").GetString());
        Assert.Equal(7, mods[1].GetProperty("nexusModId").GetInt64());
        Assert.Equal(8, mods[1].GetProperty("nexusFileId").GetInt64());

        var file = a.GetProperty("files")[0];
        Assert.Equal("A_P.pak", file.GetProperty("relativePath").GetString());
        Assert.Equal("00ff", file.GetProperty("sha256").GetString());
        Assert.Equal(3, file.GetProperty("size").GetInt64());

        // No PascalCase leaks.
        Assert.False(a.TryGetProperty("FolderName", out _));
        Assert.False(root.TryGetProperty("Mods", out _));
    }

    [Fact]
    public async Task Corrupt_file_yields_empty_manifest_and_is_set_aside()
    {
        using var temp = new TempGameRoot();
        var store = CreateStore(temp);
        await File.WriteAllTextAsync(temp.Paths.ManifestFile, "{ this is not json");

        var manifest = await store.LoadAsync();

        Assert.Empty(manifest.Mods);
        Assert.False(File.Exists(temp.Paths.ManifestFile));
        var corrupt = Directory.GetFiles(temp.DataDirectory, "manifest.json.corrupt-*");
        var file = Assert.Single(corrupt);
        Assert.Equal("{ this is not json", await File.ReadAllTextAsync(file));
    }

    [Fact]
    public async Task Null_mods_list_in_file_is_normalized()
    {
        using var temp = new TempGameRoot();
        var store = CreateStore(temp);
        await File.WriteAllTextAsync(temp.Paths.ManifestFile, """{ "schemaVersion": 1, "mods": null }""");

        var manifest = await store.LoadAsync();

        Assert.NotNull(manifest.Mods);
        Assert.Empty(manifest.Mods);
    }

    [Fact]
    public async Task Save_leaves_no_temp_files_behind_and_overwrites_atomically()
    {
        using var temp = new TempGameRoot();
        var store = CreateStore(temp);

        await store.SaveAsync(new Manifest { Mods = { new ModEntry { FolderName = "X", DisplayName = "X" } } });
        await store.SaveAsync(new Manifest { Mods = { new ModEntry { FolderName = "Y", DisplayName = "Y" } } });

        Assert.Empty(Directory.GetFiles(temp.DataDirectory, "*.tmp-*"));
        var loaded = await store.LoadAsync();
        Assert.Equal("Y", Assert.Single(loaded.Mods).FolderName);
    }
}
