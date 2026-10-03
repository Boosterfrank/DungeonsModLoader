using DungeonsModLoader.Core.Game;
using Microsoft.Extensions.Logging.Abstractions;

namespace DungeonsModLoader.Core.Tests.Game;

public class CompositeGameLocatorTests
{
    [Fact]
    public async Task Known_root_is_identified_as_the_steam_install()
    {
        using var temp = new TempDirectory();
        var root = FakeGame.CreateRoot(temp.Sub("Steam", "steamapps", "common", AppInfo.GameDisplayName));
        var steamInstall = new GameInstallation { Root = root, Source = GameSource.Steam, SteamAppId = "1912410", ExecutableNames = ["Dungeons"] };
        var locator = Create(new FakeGame.FixedSourceLocator(GameSource.Steam, steamInstall));

        var identified = await locator.IdentifyAsync(root);

        Assert.NotNull(identified);
        Assert.Equal(GameSource.Steam, identified.Source);
        Assert.Equal("1912410", identified.SteamAppId);
        Assert.Equal(GamePaths.TryNormalize(root), identified.Root);
    }

    [Fact]
    public async Task Trailing_slash_forward_slashes_and_different_casing_still_match_a_known_root()
    {
        using var temp = new TempDirectory();
        var root = FakeGame.CreateRoot(temp.Sub("Steam", "steamapps", "common", AppInfo.GameDisplayName));
        var steamInstall = new GameInstallation { Root = root, Source = GameSource.Steam, SteamAppId = "1912410" };
        var locator = Create(new FakeGame.FixedSourceLocator(GameSource.Steam, steamInstall));

        Assert.Equal(GameSource.Steam, (await locator.IdentifyAsync(root + "\\"))!.Source);
        Assert.Equal(GameSource.Steam, (await locator.IdentifyAsync(root.Replace('\\', '/') + "/"))!.Source);
        Assert.Equal(GameSource.Steam, (await locator.IdentifyAsync(root.ToUpperInvariant()))!.Source);
        Assert.Equal(GameSource.Steam, (await locator.IdentifyAsync("  " + root.ToLowerInvariant() + "  "))!.Source);
    }

    [Fact]
    public async Task Valid_unknown_root_becomes_a_manual_install_with_discovered_executables()
    {
        using var temp = new TempDirectory();
        var root = FakeGame.CreateRoot(temp.Sub("Games", "MCD2"));
        var locator = Create();

        var identified = await locator.IdentifyAsync(root + Path.DirectorySeparatorChar);

        Assert.NotNull(identified);
        Assert.Equal(GameSource.Manual, identified.Source);
        Assert.Equal(GamePaths.TryNormalize(root), identified.Root);
        Assert.Null(identified.SteamAppId);
        Assert.Null(identified.XboxAppUserModelId);
        Assert.Equal(["Dungeons", "Dungeons-Win64-Shipping"], identified.ExecutableNames);
    }

    [Fact]
    public async Task Invalid_root_returns_null()
    {
        using var temp = new TempDirectory();
        var locator = Create();

        Assert.Null(await locator.IdentifyAsync(temp.Path));
        Assert.Null(await locator.IdentifyAsync(temp.Sub("missing")));
        Assert.Null(await locator.IdentifyAsync(string.Empty));
        Assert.Null(await locator.IdentifyAsync("   "));
    }

    [Fact]
    public async Task Locate_orders_steam_first_and_deduplicates_by_normalized_root()
    {
        using var temp = new TempDirectory();
        var root = FakeGame.CreateRoot(temp.Sub("Game"));
        var xbox = new FakeGame.FixedSourceLocator(GameSource.Xbox,
            new GameInstallation { Root = root.ToUpperInvariant() + "\\", Source = GameSource.Xbox },
            new GameInstallation { Root = temp.Sub("Other"), Source = GameSource.Xbox });
        var steam = new FakeGame.FixedSourceLocator(GameSource.Steam, new GameInstallation { Root = root, Source = GameSource.Steam, SteamAppId = "1" });
        var locator = new CompositeGameLocator([xbox, steam], NullLogger<CompositeGameLocator>.Instance); // deliberately Xbox first

        var found = await locator.LocateAsync();

        Assert.Equal(2, found.Count);
        Assert.Equal(GameSource.Steam, found[0].Source);
        Assert.Equal(GamePaths.TryNormalize(root), found[0].Root);
        Assert.Equal(GameSource.Xbox, found[1].Source);
        Assert.Equal(GamePaths.TryNormalize(temp.Sub("Other")), found[1].Root);
    }

    [Fact]
    public async Task A_failing_source_does_not_hide_the_others()
    {
        using var temp = new TempDirectory();
        var root = FakeGame.CreateRoot(temp.Sub("Game"));
        var steam = new FakeGame.FixedSourceLocator(GameSource.Steam, new InvalidOperationException("boom"));
        var xbox = new FakeGame.FixedSourceLocator(GameSource.Xbox, new GameInstallation { Root = root, Source = GameSource.Xbox });
        var locator = new CompositeGameLocator([steam, xbox], NullLogger<CompositeGameLocator>.Instance);

        var found = await locator.LocateAsync();

        Assert.Equal(GameSource.Xbox, Assert.Single(found).Source);
        Assert.Equal(1, steam.Calls);
        Assert.Equal(1, xbox.Calls);
    }

    [Fact]
    public async Task Cancellation_is_propagated()
    {
        var locator = Create(new FakeGame.FixedSourceLocator(GameSource.Steam));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => locator.LocateAsync(cts.Token));
    }

    private static CompositeGameLocator Create(params IGameSourceLocator[] sources)
        => new(sources, NullLogger<CompositeGameLocator>.Instance);
}
