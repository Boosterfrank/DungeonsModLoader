using DungeonsModLoader.Core.Game;
using DungeonsModLoader.Core.Game.Xbox;
using Microsoft.Extensions.Logging.Abstractions;

namespace DungeonsModLoader.Core.Tests.Game;

public class XboxGameLocatorTests
{
    private const string FullName = "Microsoft.MinecraftDungeons2_1.1.1.0_x64__8wekyb3d8bbwe";
    private const string ExpectedAumid = "Microsoft.MinecraftDungeons2_8wekyb3d8bbwe!AppMinecraftDungeonsIIShipping";

    [Fact]
    public async Task Registry_package_with_content_root_is_found_with_an_app_user_model_id()
    {
        using var temp = new TempDirectory();
        var content = FakeGame.CreateRoot(temp.Sub("XboxGames", AppInfo.GameDisplayName, "Content"), shippingExe: false);
        FakeGame.WriteMicrosoftGameConfig(content);
        var packages = new FixedXboxPackageRepository(
            new XboxPackage("Microsoft.WindowsCalculator_11.0_x64__8wekyb3d8bbwe", temp.Sub("Calc")),
            new XboxPackage(FullName, content));

        var locator = new XboxGameLocator(packages, Array.Empty<string>(), NullLogger<XboxGameLocator>.Instance);
        var found = await locator.LocateAsync();

        var install = Assert.Single(found);
        Assert.Equal(GamePaths.TryNormalize(content), install.Root);
        Assert.Equal(GameSource.Xbox, install.Source);
        Assert.Equal(ExpectedAumid, install.XboxAppUserModelId);
        Assert.Null(install.SteamAppId);
        Assert.Equal(["Dungeons"], install.ExecutableNames);
    }

    [Fact]
    public async Task Registry_root_pointing_at_the_parent_of_content_is_accepted()
    {
        using var temp = new TempDirectory();
        var gameFolder = temp.Sub("XboxGames", AppInfo.GameDisplayName);
        var content = FakeGame.CreateRoot(Path.Combine(gameFolder, "Content"));
        FakeGame.WriteMicrosoftGameConfig(content);
        var packages = new FixedXboxPackageRepository(new XboxPackage(FullName, gameFolder));

        var locator = new XboxGameLocator(packages, Array.Empty<string>(), NullLogger<XboxGameLocator>.Instance);
        var found = await locator.LocateAsync();

        var install = Assert.Single(found);
        Assert.Equal(GamePaths.TryNormalize(content), install.Root);
        Assert.Equal(ExpectedAumid, install.XboxAppUserModelId);
    }

    [Fact]
    public async Task DefaultDisplayName_mismatch_is_rejected()
    {
        using var temp = new TempDirectory();
        var content = FakeGame.CreateRoot(temp.Sub("XboxGames", "Other Game", "Content"));
        FakeGame.WriteMicrosoftGameConfig(content, displayName: "Some Other Game", identityName: "Contoso.OtherGame");
        var packages = new FixedXboxPackageRepository(new XboxPackage("Contoso.OtherGame_1.0.0.0_x64__abc123", content));

        var locator = new XboxGameLocator(packages, [temp.Sub("XboxGames")], NullLogger<XboxGameLocator>.Instance);

        Assert.Empty(await locator.LocateAsync());
    }

    [Fact]
    public async Task Identity_name_containing_MinecraftDungeons_is_accepted_even_if_display_name_differs()
    {
        using var temp = new TempDirectory();
        var content = FakeGame.CreateRoot(temp.Sub("XboxGames", "MCD2", "Content"));
        FakeGame.WriteMicrosoftGameConfig(content, displayName: "Localized Name", identityName: "Microsoft.MinecraftDungeons2");
        var packages = new FixedXboxPackageRepository(new XboxPackage(FullName, content));

        var locator = new XboxGameLocator(packages, Array.Empty<string>(), NullLogger<XboxGameLocator>.Instance);

        Assert.Single(await locator.LocateAsync());
    }

    [Fact]
    public async Task Valid_root_without_a_config_is_rejected()
    {
        using var temp = new TempDirectory();
        var content = FakeGame.CreateRoot(temp.Sub("XboxGames", AppInfo.GameDisplayName, "Content"));
        var packages = new FixedXboxPackageRepository(new XboxPackage(FullName, content));

        var locator = new XboxGameLocator(packages, [temp.Sub("XboxGames")], NullLogger<XboxGameLocator>.Instance);

        Assert.Empty(await locator.LocateAsync());
    }

    [Fact]
    public async Task Folder_scan_finds_an_unregistered_install_without_an_app_user_model_id()
    {
        using var temp = new TempDirectory();
        var content = FakeGame.CreateRoot(temp.Sub("XboxGames", AppInfo.GameDisplayName, "Content"));
        FakeGame.WriteMicrosoftGameConfig(content);
        Directory.CreateDirectory(temp.Sub("XboxGames", "Unrelated", "Content"));

        var locator = new XboxGameLocator(new FixedXboxPackageRepository(), [temp.Sub("XboxGames"), temp.Sub("NoSuchDrive", "XboxGames")], NullLogger<XboxGameLocator>.Instance);
        var found = await locator.LocateAsync();

        var install = Assert.Single(found);
        Assert.Equal(GamePaths.TryNormalize(content), install.Root);
        Assert.Equal(GameSource.Xbox, install.Source);
        Assert.Null(install.XboxAppUserModelId);
    }

    [Fact]
    public async Task Folder_scan_resolves_the_app_user_model_id_from_a_package_with_the_same_identity()
    {
        using var temp = new TempDirectory();
        var content = FakeGame.CreateRoot(temp.Sub("XboxGames", AppInfo.GameDisplayName, "Content"));
        FakeGame.WriteMicrosoftGameConfig(content);
        // The repository knows the package but under a stale/different root folder.
        var packages = new FixedXboxPackageRepository(new XboxPackage(FullName, temp.Sub("Elsewhere")));

        var locator = new XboxGameLocator(packages, [temp.Sub("XboxGames")], NullLogger<XboxGameLocator>.Instance);
        var found = await locator.LocateAsync();

        Assert.Equal(ExpectedAumid, Assert.Single(found).XboxAppUserModelId);
    }

    [Fact]
    public async Task Registry_and_scan_results_for_the_same_root_are_deduplicated()
    {
        using var temp = new TempDirectory();
        var content = FakeGame.CreateRoot(temp.Sub("XboxGames", AppInfo.GameDisplayName, "Content"));
        FakeGame.WriteMicrosoftGameConfig(content);
        var packages = new FixedXboxPackageRepository(new XboxPackage(FullName, content.ToUpperInvariant()));

        var locator = new XboxGameLocator(packages, [temp.Sub("XboxGames")], NullLogger<XboxGameLocator>.Instance);

        Assert.Single(await locator.LocateAsync());
    }

    [Fact]
    public async Task A_throwing_package_repository_does_not_break_the_folder_scan()
    {
        using var temp = new TempDirectory();
        var content = FakeGame.CreateRoot(temp.Sub("XboxGames", AppInfo.GameDisplayName, "Content"));
        FakeGame.WriteMicrosoftGameConfig(content);

        var locator = new XboxGameLocator(new ThrowingRepository(), [temp.Sub("XboxGames")], NullLogger<XboxGameLocator>.Instance);

        Assert.Single(await locator.LocateAsync());
    }

    [Fact]
    public async Task Nothing_installed_returns_empty()
    {
        using var temp = new TempDirectory();
        var locator = new XboxGameLocator(new FixedXboxPackageRepository(), [temp.Sub("XboxGames")], NullLogger<XboxGameLocator>.Instance);

        Assert.Empty(await locator.LocateAsync());
    }

    private sealed class ThrowingRepository : IXboxPackageRepository
    {
        public IReadOnlyList<XboxPackage> GetInstalledPackages() => throw new InvalidOperationException("registry exploded");
    }
}
