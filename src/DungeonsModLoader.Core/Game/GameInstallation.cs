using System.Text.Json.Serialization;

namespace DungeonsModLoader.Core.Game;

/// <summary>
/// A located Minecraft Dungeons II installation. <see cref="Root"/> is the folder that contains the
/// <c>Dungeons</c> folder (for Steam: <c>...\steamapps\common\Minecraft Dungeons II</c>; for the Xbox app /
/// Minecraft Launcher: <c>...\XboxGames\Minecraft Dungeons II\Content</c>).
/// </summary>
public sealed record GameInstallation
{
    /// <summary>Folder that contains <c>Dungeons\Content\Paks</c>.</summary>
    public required string Root { get; init; }

    public required GameSource Source { get; init; }

    /// <summary>Steam App ID read from the appmanifest (only when <see cref="Source"/> is Steam).</summary>
    public string? SteamAppId { get; init; }

    /// <summary>
    /// AppUserModelId of the packaged app, e.g. <c>Microsoft.MinecraftDungeons2_8wekyb3d8bbwe!AppMinecraftDungeonsIIShipping</c>
    /// (only when <see cref="Source"/> is Xbox). Launched via <c>shell:AppsFolder\{id}</c>.
    /// </summary>
    public string? XboxAppUserModelId { get; init; }

    /// <summary>
    /// Process names (file name without extension) of the game's executables, discovered on disk at detection
    /// time, e.g. "Dungeons" and "Dungeons-Win64-Shipping". Used to detect whether the game is running.
    /// </summary>
    public IReadOnlyList<string> ExecutableNames { get; init; } = Array.Empty<string>();

    [JsonIgnore] public string DungeonsDirectory => Path.Combine(Root, "Dungeons");
    [JsonIgnore] public string PaksDirectory => Path.Combine(Root, "Dungeons", "Content", "Paks");

    /// <summary>Enabled mods live here, one subfolder per mod.</summary>
    [JsonIgnore] public string ModsDirectory => Path.Combine(PaksDirectory, "~mods");

    /// <summary>
    /// Disabled mods live here: same drive as <see cref="ModsDirectory"/> (so enabling is a rename, not a copy)
    /// but outside <c>Paks</c>, so the game ignores them.
    /// </summary>
    [JsonIgnore] public string DisabledModsDirectory => Path.Combine(DungeonsDirectory, AppInfo.FolderName + "_Disabled");

    /// <summary>Folder name of the game's main executable candidates, in launch-preference order.</summary>
    public static readonly string[] KnownExecutableNames = ["Dungeons", "Dungeons-Win64-Shipping"];

    /// <summary>True when <paramref name="root"/> contains <c>Dungeons\Content\Paks</c>.</summary>
    public static bool IsValidRoot(string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        try
        {
            return Directory.Exists(Path.Combine(root, "Dungeons", "Content", "Paks"));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// Finds the game executables under <paramref name="root"/> (root itself and <c>Dungeons\Binaries\Win64</c>),
    /// returning their names without extension. Never throws; returns the known names when nothing is found.
    /// </summary>
    public static IReadOnlyList<string> DiscoverExecutableNames(string root)
    {
        var names = new List<string>();
        foreach (var dir in new[] { root, Path.Combine(root, "Dungeons", "Binaries", "Win64") })
        {
            try
            {
                if (!Directory.Exists(dir))
                {
                    continue;
                }

                foreach (var exe in Directory.EnumerateFiles(dir, "Dungeons*.exe", SearchOption.TopDirectoryOnly))
                {
                    var name = Path.GetFileNameWithoutExtension(exe);
                    if (!names.Contains(name, StringComparer.OrdinalIgnoreCase))
                    {
                        names.Add(name);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Ignore unreadable folders; fall back to the known names below.
            }
        }

        return names.Count > 0 ? names : KnownExecutableNames;
    }

    /// <summary>Full path of the executable to start for a manual launch, or null if none exists.</summary>
    public string? FindLaunchExecutable()
    {
        // Executables discovered on disk first (launcher stub before the shipping binary), then the known names.
        var names = ExecutableNames
            .Concat(KnownExecutableNames)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var name in names)
        {
            foreach (var dir in new[] { Root, Path.Combine(Root, "Dungeons", "Binaries", "Win64") })
            {
                var candidate = Path.Combine(dir, name + ".exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}
