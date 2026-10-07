using System.Text.RegularExpressions;

namespace DungeonsModLoader.Core.AppUpdates;

/// <summary>Decides whether a GitHub release is an update for the running app and which attached file to download.</summary>
public static partial class AppUpdateResolver
{
    /// <summary>File name prefix of the installer built by <c>build.ps1</c> (<c>DungeonsModLoader-Setup-0.2.0.exe</c>).</summary>
    public const string InstallerPrefix = AppInfo.DisplayName + "-Setup-";

    /// <summary>
    /// The update described by <paramref name="release"/>, or null when the release is a draft or pre-release, is
    /// not newer than <paramref name="currentVersion"/>, or has no installer attached.
    /// </summary>
    public static AppUpdateInfo? Resolve(GitHubRelease? release, string currentVersion)
    {
        if (release is null || release.IsDraft || release.IsPrerelease)
        {
            return null;
        }

        if (!AppVersions.TryParse(release.TagName, out var version) && !AppVersions.TryParse(release.Name, out version))
        {
            return null;
        }

        if (!AppVersions.IsNewer(AppVersions.ToText(version), currentVersion))
        {
            return null;
        }

        var installer = release.Assets
            .Where(a => InstallerName().IsMatch(a.Name))
            .OrderByDescending(a => a.Name.Contains(AppVersions.ToText(version), StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
        if (installer is null)
        {
            return null;
        }

        return new AppUpdateInfo(version, AppVersions.ToText(version), release.HtmlUrl, installer, string.IsNullOrWhiteSpace(release.Body) ? null : release.Body.Trim());
    }

    [GeneratedRegex(@"^DungeonsModLoader-Setup-.*\.exe$", RegexOptions.IgnoreCase)]
    private static partial Regex InstallerName();
}
