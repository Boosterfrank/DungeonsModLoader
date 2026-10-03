namespace DungeonsModLoader.Core.Game;

/// <summary>Where the game installation came from; decides how it is launched.</summary>
public enum GameSource
{
    /// <summary>Picked by hand; launched by starting the game executable directly.</summary>
    Manual = 0,

    /// <summary>Steam install (appmanifest found); launched through <c>steam://rungameid/{appId}</c>.</summary>
    Steam = 1,

    /// <summary>Xbox app / Minecraft Launcher (Microsoft Store packaged) install; launched through its AppUserModelId.</summary>
    Xbox = 2,
}
