using DungeonsModLoader.Core.Game;
using DungeonsModLoader.Core.Game.Steam;
using Microsoft.Extensions.Logging.Abstractions;

namespace DungeonsModLoader.Core.Tests.Game;

public class SteamGameLocatorTests
{
    private const string TestAppId = "1912410";

    [Fact]
    public async Task Finds_the_game_in_a_second_library_listed_in_libraryfolders()
    {
        using var temp = new TempDirectory();
        var steam = CreateSteamRoot(temp.Sub("Steam"));
        var library = Directory.CreateDirectory(temp.Sub("Library", "steamapps")).Parent!.FullName;
        WriteLibraryFolders(steam, library);
        var gameRoot = WriteGame(library, TestAppId);

        var locator = new SteamGameLocator(steam, NullLogger<SteamGameLocator>.Instance);
        var found = await locator.LocateAsync();

        var install = Assert.Single(found);
        Assert.Equal(GamePaths.TryNormalize(gameRoot), install.Root);
        Assert.Equal(GameSource.Steam, install.Source);
        Assert.Equal(TestAppId, install.SteamAppId);
        Assert.Null(install.XboxAppUserModelId);
        Assert.Contains("Dungeons", install.ExecutableNames);
        Assert.Contains("Dungeons-Win64-Shipping", install.ExecutableNames);
    }

    [Fact]
    public async Task Finds_the_game_in_the_steam_root_library_itself()
    {
        using var temp = new TempDirectory();
        var steam = CreateSteamRoot(temp.Sub("Steam"));
        WriteLibraryFolders(steam); // only lists the Steam folder
        var gameRoot = WriteGame(steam, "424242");

        var locator = new SteamGameLocator(steam, NullLogger<SteamGameLocator>.Instance);
        var found = await locator.LocateAsync();

        var install = Assert.Single(found);
        Assert.Equal(GamePaths.TryNormalize(gameRoot), install.Root);
        Assert.Equal("424242", install.SteamAppId);
    }

    [Fact]
    public async Task Steam_root_is_searched_even_without_libraryfolders_file()
    {
        using var temp = new TempDirectory();
        var steam = CreateSteamRoot(temp.Sub("Steam"));
        var gameRoot = WriteGame(steam, TestAppId);

        var locator = new SteamGameLocator(steam, NullLogger<SteamGameLocator>.Instance);
        var found = await locator.LocateAsync();

        Assert.Equal(GamePaths.TryNormalize(gameRoot), Assert.Single(found).Root);
    }

    [Fact]
    public async Task Steam_path_from_registry_is_normalized_from_forward_slashes_and_odd_casing()
    {
        using var temp = new TempDirectory();
        var steam = CreateSteamRoot(temp.Sub("Steam"));
        var gameRoot = WriteGame(steam, TestAppId);
        var registryStyle = steam.Replace('\\', '/').ToLowerInvariant() + "/";

        var locator = new SteamGameLocator(new FixedSteamRegistry(registryStyle), NullLogger<SteamGameLocator>.Instance);
        var found = await locator.LocateAsync();

        var install = Assert.Single(found);
        Assert.True(GamePaths.AreSameFolder(gameRoot, install.Root));
        Assert.DoesNotContain('/', install.Root);
        Assert.False(install.Root.EndsWith(Path.DirectorySeparatorChar));
    }

    [Fact]
    public async Task A_library_that_does_not_exist_is_skipped()
    {
        using var temp = new TempDirectory();
        var steam = CreateSteamRoot(temp.Sub("Steam"));
        WriteLibraryFolders(steam, temp.Sub("Missing", "Library"), Path.Combine("Z:", "Nope", "Steam"));
        var gameRoot = WriteGame(steam, TestAppId);

        var locator = new SteamGameLocator(steam, NullLogger<SteamGameLocator>.Instance);
        var libraries = locator.GetLibraryPaths(steam);
        var found = await locator.LocateAsync();

        Assert.Equal([GamePaths.TryNormalize(steam)!], libraries);
        Assert.Equal(GamePaths.TryNormalize(gameRoot), Assert.Single(found).Root);
    }

    [Fact]
    public async Task No_manifest_means_no_result()
    {
        using var temp = new TempDirectory();
        var steam = CreateSteamRoot(temp.Sub("Steam"));
        WriteLibraryFolders(steam);
        FakeGame.CreateRoot(Path.Combine(steam, "steamapps", "common", AppInfo.GameDisplayName)); // folder without .acf

        var locator = new SteamGameLocator(steam, NullLogger<SteamGameLocator>.Instance);

        Assert.Empty(await locator.LocateAsync());
    }

    [Fact]
    public async Task Manifests_for_other_games_are_ignored()
    {
        using var temp = new TempDirectory();
        var steam = CreateSteamRoot(temp.Sub("Steam"));
        WriteManifest(steam, "250820", "SteamVR");
        FakeGame.CreateRoot(Path.Combine(steam, "steamapps", "common", "SteamVR"));

        var locator = new SteamGameLocator(steam, NullLogger<SteamGameLocator>.Instance);

        Assert.Empty(await locator.LocateAsync());
    }

    [Fact]
    public async Task Manifest_whose_folder_is_not_a_valid_root_is_ignored()
    {
        using var temp = new TempDirectory();
        var steam = CreateSteamRoot(temp.Sub("Steam"));
        WriteManifest(steam, TestAppId, AppInfo.GameDisplayName);
        Directory.CreateDirectory(Path.Combine(steam, "steamapps", "common", AppInfo.GameDisplayName)); // no Dungeons\Content\Paks

        var locator = new SteamGameLocator(steam, NullLogger<SteamGameLocator>.Instance);

        Assert.Empty(await locator.LocateAsync());
    }

    [Fact]
    public async Task Missing_steam_returns_empty_without_throwing()
    {
        var nullRegistry = new SteamGameLocator(new FixedSteamRegistry(null), NullLogger<SteamGameLocator>.Instance);
        var missingFolder = new SteamGameLocator(Path.Combine(Path.GetTempPath(), "dml-does-not-exist-" + Guid.NewGuid().ToString("N")), NullLogger<SteamGameLocator>.Instance);
        var throwingRegistry = new SteamGameLocator(new ThrowingRegistry(), NullLogger<SteamGameLocator>.Instance);

        Assert.Empty(await nullRegistry.LocateAsync());
        Assert.Empty(await missingFolder.LocateAsync());
        Assert.Empty(await throwingRegistry.LocateAsync());
    }

    [Fact]
    public void Old_flat_libraryfolders_format_is_understood()
    {
        using var temp = new TempDirectory();
        var steam = CreateSteamRoot(temp.Sub("Steam"));
        var library = Directory.CreateDirectory(temp.Sub("OldLibrary", "steamapps")).Parent!.FullName;
        File.WriteAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf"), $$"""
            "LibraryFolders"
            {
                "TimeNextStatsReport"   "1600000000"
                "ContentStatsID"        "-1"
                "1"     "{{library.Replace("\\", "\\\\")}}"
            }
            """);

        var locator = new SteamGameLocator(steam, NullLogger<SteamGameLocator>.Instance);
        var libraries = locator.GetLibraryPaths(steam);

        Assert.Equal([GamePaths.TryNormalize(library)!, GamePaths.TryNormalize(steam)!], libraries);
    }

    [Fact]
    public void Libraries_are_deduplicated_case_insensitively()
    {
        using var temp = new TempDirectory();
        var steam = CreateSteamRoot(temp.Sub("Steam"));
        WriteLibraryFolders(steam, steam.ToUpperInvariant(), steam + Path.DirectorySeparatorChar);

        var locator = new SteamGameLocator(steam, NullLogger<SteamGameLocator>.Instance);

        Assert.Single(locator.GetLibraryPaths(steam));
    }

    private static string CreateSteamRoot(string path)
    {
        Directory.CreateDirectory(Path.Combine(path, "steamapps"));
        return path;
    }

    /// <summary>Writes a new-format libraryfolders.vdf listing the Steam root plus <paramref name="extraLibraries"/>.</summary>
    private static void WriteLibraryFolders(string steam, params string[] extraLibraries)
    {
        var builder = new System.Text.StringBuilder();
        builder.Append("\"libraryfolders\"\n{\n");
        var index = 0;
        foreach (var library in new[] { steam }.Concat(extraLibraries))
        {
            builder.Append($"\t\"{index++}\"\n\t{{\n\t\t\"path\"\t\t\"{library.Replace("\\", "\\\\")}\"\n\t\t\"label\"\t\t\"\"\n\t\t\"apps\"\n\t\t{{\n\t\t\t\"228980\"\t\t\"171517476\"\n\t\t}}\n\t}}\n");
        }

        builder.Append("}\n");
        File.WriteAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf"), builder.ToString());
    }

    private static void WriteManifest(string library, string appId, string installDir)
    {
        File.WriteAllText(Path.Combine(library, "steamapps", $"appmanifest_{appId}.acf"), $$"""
            "AppState"
            {
            	"appid"		"{{appId}}"
            	"Universe"		"1"
            	"name"		"{{installDir}}"
            	"StateFlags"		"4"
            	"installdir"		"{{installDir}}"
            	"InstalledDepots"
            	{
            		"{{appId}}1"
            		{
            			"manifest"		"1012784868868940449"
            		}
            	}
            }
            """);
    }

    /// <summary>Writes the manifest and a valid fake game root into <paramref name="library"/>; returns the root.</summary>
    private static string WriteGame(string library, string appId)
    {
        WriteManifest(library, appId, AppInfo.GameDisplayName);
        return FakeGame.CreateRoot(Path.Combine(library, "steamapps", "common", AppInfo.GameDisplayName));
    }

    private sealed class ThrowingRegistry : ISteamRegistry
    {
        public string? GetSteamPath() => throw new InvalidOperationException("registry exploded");
    }
}
