using DungeonsModLoader.Core.Game;

namespace DungeonsModLoader.Core.Tests.Game;

public class GameInstallationTests
{
    [Fact]
    public void IsValidRoot_requires_Dungeons_Content_Paks()
    {
        using var temp = new TempDirectory();
        var valid = FakeGame.CreateRoot(temp.Sub("valid"), rootExe: false, shippingExe: false);
        Directory.CreateDirectory(temp.Sub("partial", "Dungeons", "Content"));

        Assert.True(GameInstallation.IsValidRoot(valid));
        Assert.False(GameInstallation.IsValidRoot(temp.Sub("partial")));
        Assert.False(GameInstallation.IsValidRoot(temp.Sub("missing")));
        Assert.False(GameInstallation.IsValidRoot(null));
        Assert.False(GameInstallation.IsValidRoot(string.Empty));
        Assert.False(GameInstallation.IsValidRoot("   "));
        Assert.False(GameInstallation.IsValidRoot("C:\\bad\0path"));
    }

    [Fact]
    public void DiscoverExecutableNames_finds_both_executable_locations()
    {
        using var temp = new TempDirectory();
        var root = FakeGame.CreateRoot(temp.Sub("game"));

        var names = GameInstallation.DiscoverExecutableNames(root);

        Assert.Equal(["Dungeons", "Dungeons-Win64-Shipping"], names);
    }

    [Fact]
    public void DiscoverExecutableNames_falls_back_to_known_names_when_nothing_is_on_disk()
    {
        using var temp = new TempDirectory();
        var root = FakeGame.CreateRoot(temp.Sub("game"), rootExe: false, shippingExe: false);

        Assert.Equal(GameInstallation.KnownExecutableNames, GameInstallation.DiscoverExecutableNames(root));
        Assert.Equal(GameInstallation.KnownExecutableNames, GameInstallation.DiscoverExecutableNames(temp.Sub("missing")));
    }

    [Fact]
    public void FindLaunchExecutable_prefers_the_root_launcher_then_the_shipping_exe()
    {
        using var temp = new TempDirectory();
        var both = new GameInstallation { Root = FakeGame.CreateRoot(temp.Sub("both")), Source = GameSource.Manual };
        var shippingOnly = new GameInstallation { Root = FakeGame.CreateRoot(temp.Sub("shipping"), rootExe: false), Source = GameSource.Manual };
        var none = new GameInstallation { Root = FakeGame.CreateRoot(temp.Sub("none"), rootExe: false, shippingExe: false), Source = GameSource.Manual };

        Assert.Equal(Path.Combine(both.Root, "Dungeons.exe"), both.FindLaunchExecutable());
        Assert.Equal(Path.Combine(shippingOnly.Root, "Dungeons", "Binaries", "Win64", "Dungeons-Win64-Shipping.exe"), shippingOnly.FindLaunchExecutable());
        Assert.Null(none.FindLaunchExecutable());
    }

    [Fact]
    public void Derived_directories_hang_off_the_root()
    {
        var install = new GameInstallation { Root = Path.Combine("C:", "Game"), Source = GameSource.Manual };

        Assert.Equal(Path.Combine("C:", "Game", "Dungeons", "Content", "Paks"), install.PaksDirectory);
        Assert.Equal(Path.Combine("C:", "Game", "Dungeons", "Content", "Paks", "~mods"), install.ModsDirectory);
        Assert.Equal(Path.Combine("C:", "Game", "Dungeons", AppInfo.FolderName + "_Disabled"), install.DisabledModsDirectory);
    }
}

public class GamePathsTests
{
    [Fact]
    public void Normalizes_slashes_trailing_separators_and_whitespace()
    {
        var expected = Path.GetFullPath(Path.Combine("C:", "Games", "X"));

        Assert.Equal(expected, GamePaths.TryNormalize("C:/Games/X/"));
        Assert.Equal(expected, GamePaths.TryNormalize(" C:\\Games\\X\\ "));
        Assert.Equal(expected, GamePaths.TryNormalize("C:\\Games\\X"));
    }

    [Fact]
    public void Keeps_the_separator_on_a_drive_root()
    {
        Assert.Equal("C:\\", GamePaths.TryNormalize("C:\\"));
        Assert.Equal("C:\\", GamePaths.TryNormalize("C:/"));
    }

    [Fact]
    public void Returns_null_for_empty_or_invalid_paths()
    {
        Assert.Null(GamePaths.TryNormalize(null));
        Assert.Null(GamePaths.TryNormalize(""));
        Assert.Null(GamePaths.TryNormalize("  "));
        Assert.Null(GamePaths.TryNormalize("C:\\bad\0path"));
    }

    [Fact]
    public void AreSameFolder_is_case_insensitive_and_false_for_unusable_input()
    {
        Assert.True(GamePaths.AreSameFolder("C:/Games/X/", "c:\\games\\x"));
        Assert.False(GamePaths.AreSameFolder("C:\\Games\\X", "C:\\Games\\Y"));
        Assert.False(GamePaths.AreSameFolder(null, null));
        Assert.False(GamePaths.AreSameFolder("", ""));
    }
}
