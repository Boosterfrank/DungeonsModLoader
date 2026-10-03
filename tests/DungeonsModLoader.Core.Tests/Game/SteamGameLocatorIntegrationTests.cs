using DungeonsModLoader.Core.Game;
using DungeonsModLoader.Core.Game.Steam;
using Microsoft.Extensions.Logging.Abstractions;

namespace DungeonsModLoader.Core.Tests.Game;

/// <summary>
/// Looks at the real machine (Steam registry + libraries). Opt in with <c>DML_INTEGRATION=1</c>; skipped otherwise.
/// </summary>
public class SteamGameLocatorIntegrationTests
{
    [IntegrationFact]
    public async Task Real_steam_install_is_found_with_the_known_app_id()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // Steam detection reads the Windows registry.
        }

        var locator = new SteamGameLocator(new WindowsSteamRegistry(), NullLogger<SteamGameLocator>.Instance);
        var found = await locator.LocateAsync();

        var install = Assert.Single(found);
        Assert.Equal(GameSource.Steam, install.Source);
        Assert.Equal("1912410", install.SteamAppId);
        Assert.True(GameInstallation.IsValidRoot(install.Root));
        Assert.EndsWith(AppInfo.GameDisplayName, install.Root, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Dungeons", install.ExecutableNames);
        Assert.Contains("Dungeons-Win64-Shipping", install.ExecutableNames);
        Assert.NotNull(install.FindLaunchExecutable());
    }
}
