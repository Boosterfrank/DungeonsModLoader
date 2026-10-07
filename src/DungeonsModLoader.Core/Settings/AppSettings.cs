using DungeonsModLoader.Core.Game;

namespace DungeonsModLoader.Core.Settings;

/// <summary>Persisted user settings (<c>settings.json</c>). Mutable; saved through <see cref="ISettingsStore"/>.</summary>
public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>True once the first-run setup has been completed (or skipped past the game-folder step).</summary>
    public bool FirstRunCompleted { get; set; }

    public string? GameRootPath { get; set; }
    public GameSource GameSource { get; set; } = GameSource.Manual;
    public string? SteamAppId { get; set; }
    public string? XboxAppUserModelId { get; set; }

    /// <summary>Name of the active profile; "Default" always exists.</summary>
    public string ActiveProfile { get; set; } = "Default";

    public bool CheckForAppUpdates { get; set; } = true;
    public DateTimeOffset? LastModUpdateCheckUtc { get; set; }
    public DateTimeOffset? LastAppUpdateCheckUtc { get; set; }

    /// <summary>
    /// How many times the app was started while a newer release was known. Reset to 0 when a start finds the app
    /// up to date; past <see cref="AppUpdates.AppUpdatePolicy.FreeOutdatedLaunches"/> the update becomes mandatory.
    /// </summary>
    public int OutdatedLaunchCount { get; set; }

    /// <summary>
    /// Rebuilds the <see cref="GameInstallation"/> from the stored fields, re-discovering the executables.
    /// Returns null when no valid game root is stored.
    /// </summary>
    public GameInstallation? ToGameInstallation()
    {
        if (!GameInstallation.IsValidRoot(GameRootPath))
        {
            return null;
        }

        return new GameInstallation
        {
            Root = GameRootPath!,
            Source = GameSource,
            SteamAppId = SteamAppId,
            XboxAppUserModelId = XboxAppUserModelId,
            ExecutableNames = GameInstallation.DiscoverExecutableNames(GameRootPath!),
        };
    }

    /// <summary>Stores the fields of <paramref name="installation"/> (or clears them when null).</summary>
    public void ApplyGameInstallation(GameInstallation? installation)
    {
        GameRootPath = installation?.Root;
        GameSource = installation?.Source ?? GameSource.Manual;
        SteamAppId = installation?.SteamAppId;
        XboxAppUserModelId = installation?.XboxAppUserModelId;
    }
}
