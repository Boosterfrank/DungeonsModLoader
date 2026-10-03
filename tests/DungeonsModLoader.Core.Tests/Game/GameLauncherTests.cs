using DungeonsModLoader.Core.Game;
using Microsoft.Extensions.Logging.Abstractions;

namespace DungeonsModLoader.Core.Tests.Game;

public class GameLauncherTests
{
    [Fact]
    public async Task Manual_install_without_an_executable_fails_with_a_friendly_message()
    {
        using var temp = new TempDirectory();
        var root = FakeGame.CreateRoot(temp.Sub("game"), rootExe: false, shippingExe: false);
        var launcher = new GameLauncher(NullLogger<GameLauncher>.Instance);

        var ex = await Assert.ThrowsAsync<GameLaunchException>(() => launcher.LaunchAsync(new GameInstallation { Root = root, Source = GameSource.Manual }));

        Assert.Equal($"Could not find the game executable in {root}.", ex.Message);
    }

    [Fact]
    public async Task Xbox_install_without_an_app_user_model_id_falls_back_to_the_executable()
    {
        using var temp = new TempDirectory();
        var root = FakeGame.CreateRoot(temp.Sub("game"), rootExe: false, shippingExe: false);
        var launcher = new GameLauncher(NullLogger<GameLauncher>.Instance);

        // No executable on disk either, so the fallback surfaces the executable error rather than an Xbox one.
        var ex = await Assert.ThrowsAsync<GameLaunchException>(() => launcher.LaunchAsync(new GameInstallation { Root = root, Source = GameSource.Xbox, XboxAppUserModelId = null }));

        Assert.Contains("Could not find the game executable", ex.Message);
    }

    [Fact]
    public async Task Steam_install_without_an_app_id_falls_back_to_the_executable()
    {
        using var temp = new TempDirectory();
        var root = FakeGame.CreateRoot(temp.Sub("game"), rootExe: false, shippingExe: false);
        var launcher = new GameLauncher(NullLogger<GameLauncher>.Instance);

        var ex = await Assert.ThrowsAsync<GameLaunchException>(() => launcher.LaunchAsync(new GameInstallation { Root = root, Source = GameSource.Steam, SteamAppId = " " }));

        Assert.Contains("Could not find the game executable", ex.Message);
    }

    [Fact]
    public async Task Executable_that_windows_refuses_to_start_fails_with_a_friendly_message()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // relies on ShellExecute rejecting a non-PE file
        }

        using var temp = new TempDirectory();
        var root = FakeGame.CreateRoot(temp.Sub("game"), rootExe: true, shippingExe: false); // Dungeons.exe is a text stub, not a program
        var launcher = new GameLauncher(NullLogger<GameLauncher>.Instance);

        var ex = await Assert.ThrowsAsync<GameLaunchException>(() => launcher.LaunchAsync(new GameInstallation { Root = root, Source = GameSource.Manual }));

        Assert.StartsWith("Windows could not start Dungeons.exe", ex.Message);
        Assert.NotNull(ex.InnerException);
    }

    [Fact]
    public async Task Cancelled_token_is_honoured_before_anything_starts()
    {
        var launcher = new GameLauncher(NullLogger<GameLauncher>.Instance);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => launcher.LaunchAsync(new GameInstallation { Root = "C:\\x", Source = GameSource.Steam, SteamAppId = "1" }, cts.Token));
    }

    [Fact]
    public async Task Null_installation_is_rejected()
    {
        var launcher = new GameLauncher(NullLogger<GameLauncher>.Instance);

        await Assert.ThrowsAsync<ArgumentNullException>(() => launcher.LaunchAsync(null!));
    }
}
