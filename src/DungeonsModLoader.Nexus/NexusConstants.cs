namespace DungeonsModLoader.Nexus;

/// <summary>
/// Non-secret Nexus Mods configuration. Nothing in here is a credential: the user's API key is stored
/// encrypted per user (milestone 5) and never lives in the repository.
/// </summary>
public static class NexusConstants
{
    /// <summary>Game domain on Nexus Mods (https://www.nexusmods.com/games/minecraftdungeons2), verified 2026-10-03.</summary>
    public const string GameDomain = "minecraftdungeons2";

    /// <summary>Numeric game id on Nexus Mods for <see cref="GameDomain"/> (from the v2 GraphQL API).</summary>
    public const int GameId = 10391;

    /// <summary>
    /// Application slug issued by Nexus Mods when the app is registered for SSO. Empty until registration;
    /// while empty the app offers personal-API-key login only.
    /// </summary>
    public const string AppSlug = "";

    public const string WebsiteBaseUrl = "https://www.nexusmods.com";

    /// <summary>Public page of a mod, e.g. https://www.nexusmods.com/minecraftdungeons2/mods/10.</summary>
    public static string ModPageUrl(long modId) => $"{WebsiteBaseUrl}/{GameDomain}/mods/{modId}";

    /// <summary>Files tab of a mod page (where free users click "Mod Manager Download").</summary>
    public static string ModFilesUrl(long modId) => $"{WebsiteBaseUrl}/{GameDomain}/mods/{modId}?tab=files";
}
