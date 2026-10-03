using System.Reflection;

namespace DungeonsModLoader.Core;

/// <summary>
/// Identity of the application. The display name lives here (and only here) so it can be renamed in one place.
/// </summary>
public static class AppInfo
{
    /// <summary>User-facing product name.</summary>
    public const string DisplayName = "DungeonsModLoader";

    /// <summary>Folder name used under %LOCALAPPDATA% and in the game folder (no spaces, path-safe).</summary>
    public const string FolderName = "DungeonsModLoader";

    /// <summary>Name of the game this manager targets.</summary>
    public const string GameDisplayName = "Minecraft Dungeons II";

    /// <summary>GitHub repository used for app self-updates ("owner/repo").</summary>
    public const string GitHubRepository = "Boosterfrank/DungeonsModLoader";

    /// <summary>Assembly version, e.g. "0.1.0".</summary>
    public static string Version { get; } = ResolveVersion();

    private static string ResolveVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(AppInfo).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            // Strip any "+commitsha" suffix that SourceLink/MSBuild may append.
            var plus = informational.IndexOf('+');
            return plus > 0 ? informational[..plus] : informational;
        }

        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
