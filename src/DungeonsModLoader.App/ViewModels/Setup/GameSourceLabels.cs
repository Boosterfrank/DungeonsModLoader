using DungeonsModLoader.Core.Game;

namespace DungeonsModLoader.App.ViewModels.Setup;

/// <summary>User-facing wording for <see cref="GameSource"/>, shared by the setup wizard and the Settings page.</summary>
public static class GameSourceLabels
{
    /// <summary>Short badge text: "Steam", "Xbox app" or "Manual".</summary>
    public static string For(GameSource source) => source switch
    {
        GameSource.Steam => "Steam",
        GameSource.Xbox => "Xbox app",
        _ => "Manual",
    };

    /// <summary>One line explaining how the game will be started for this source.</summary>
    public static string LaunchHintFor(GameSource source) => source switch
    {
        GameSource.Steam => "Launches through Steam",
        GameSource.Xbox => "Launches through the Xbox app",
        _ => "Launches the game executable",
    };
}
