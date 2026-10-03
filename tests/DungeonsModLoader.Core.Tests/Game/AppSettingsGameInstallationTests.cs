using DungeonsModLoader.Core.Game;
using DungeonsModLoader.Core.Settings;
using DungeonsModLoader.Core.Storage;

namespace DungeonsModLoader.Core.Tests.Game;

public class AppSettingsGameInstallationTests
{
    [Fact]
    public void Round_trips_a_steam_installation_through_the_settings_fields()
    {
        using var temp = new TempDirectory();
        var root = FakeGame.CreateRoot(temp.Sub("game"));
        var original = new GameInstallation { Root = root, Source = GameSource.Steam, SteamAppId = "1912410", ExecutableNames = ["stale"] };
        var settings = new AppSettings();

        settings.ApplyGameInstallation(original);
        var restored = settings.ToGameInstallation();

        Assert.Equal(root, settings.GameRootPath);
        Assert.Equal(GameSource.Steam, settings.GameSource);
        Assert.Equal("1912410", settings.SteamAppId);
        Assert.Null(settings.XboxAppUserModelId);

        Assert.NotNull(restored);
        Assert.Equal(root, restored.Root);
        Assert.Equal(GameSource.Steam, restored.Source);
        Assert.Equal("1912410", restored.SteamAppId);
        Assert.Null(restored.XboxAppUserModelId);
        Assert.Equal(["Dungeons", "Dungeons-Win64-Shipping"], restored.ExecutableNames); // re-discovered, not the stale list
    }

    [Fact]
    public void Round_trips_an_xbox_installation()
    {
        using var temp = new TempDirectory();
        var root = FakeGame.CreateRoot(temp.Sub("Content"), shippingExe: false);
        var settings = new AppSettings();

        settings.ApplyGameInstallation(new GameInstallation { Root = root, Source = GameSource.Xbox, XboxAppUserModelId = "Microsoft.MinecraftDungeons2_8wekyb3d8bbwe!AppMinecraftDungeonsIIShipping" });
        var restored = settings.ToGameInstallation();

        Assert.NotNull(restored);
        Assert.Equal(GameSource.Xbox, restored.Source);
        Assert.Equal("Microsoft.MinecraftDungeons2_8wekyb3d8bbwe!AppMinecraftDungeonsIIShipping", restored.XboxAppUserModelId);
        Assert.Null(restored.SteamAppId);
        Assert.Equal(["Dungeons"], restored.ExecutableNames);
    }

    [Fact]
    public void Invalid_or_missing_root_yields_null()
    {
        using var temp = new TempDirectory();
        var settings = new AppSettings { GameRootPath = temp.Sub("nope"), GameSource = GameSource.Manual };

        Assert.Null(settings.ToGameInstallation());
        Assert.Null(new AppSettings().ToGameInstallation());
    }

    [Fact]
    public void Applying_null_clears_the_fields()
    {
        var settings = new AppSettings { GameRootPath = "C:\\x", GameSource = GameSource.Steam, SteamAppId = "1", XboxAppUserModelId = "a!b" };

        settings.ApplyGameInstallation(null);

        Assert.Null(settings.GameRootPath);
        Assert.Equal(GameSource.Manual, settings.GameSource);
        Assert.Null(settings.SteamAppId);
        Assert.Null(settings.XboxAppUserModelId);
    }

    [Fact]
    public async Task Settings_survive_a_json_round_trip_with_the_shared_options()
    {
        using var temp = new TempDirectory();
        var root = FakeGame.CreateRoot(temp.Sub("game"));
        var settings = new AppSettings { FirstRunCompleted = true };
        settings.ApplyGameInstallation(new GameInstallation { Root = root, Source = GameSource.Xbox, XboxAppUserModelId = "fam!app" });
        var path = temp.Sub("settings.json");

        await AtomicJsonFile.WriteAsync(path, settings);
        var loaded = await AtomicJsonFile.ReadAsync<AppSettings>(path);
        var json = await File.ReadAllTextAsync(path);

        Assert.Contains("\"gameSource\": \"xbox\"", json); // camelCase enum string
        Assert.NotNull(loaded);
        Assert.Equal(GameSource.Xbox, loaded.GameSource);
        Assert.Equal(root, loaded.ToGameInstallation()!.Root);
        Assert.Equal("fam!app", loaded.ToGameInstallation()!.XboxAppUserModelId);
    }
}
