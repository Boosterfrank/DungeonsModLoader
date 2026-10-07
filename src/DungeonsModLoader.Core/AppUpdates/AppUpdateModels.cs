namespace DungeonsModLoader.Core.AppUpdates;

/// <summary>A file attached to a GitHub release.</summary>
public sealed record GitHubReleaseAsset(string Name, Uri DownloadUrl, long Size);

/// <summary>The parts of a GitHub release the app cares about.</summary>
public sealed record GitHubRelease(
    string TagName,
    string? Name,
    Uri HtmlUrl,
    bool IsPrerelease,
    bool IsDraft,
    DateTimeOffset? PublishedAt,
    string? Body,
    IReadOnlyList<GitHubReleaseAsset> Assets);

/// <summary>A newer version of the app that can be installed.</summary>
/// <param name="Version">Parsed version of the release.</param>
/// <param name="VersionText">The version as shown to the user ("0.2.0").</param>
/// <param name="ReleaseUrl">The release page (release notes).</param>
/// <param name="Installer">The setup executable attached to the release.</param>
/// <param name="Notes">Release notes (Markdown as written on GitHub), when any.</param>
public sealed record AppUpdateInfo(Version Version, string VersionText, Uri ReleaseUrl, GitHubReleaseAsset Installer, string? Notes);

/// <summary>GitHub could not be asked for releases (offline, rate limited, unexpected answer). The message is phrased for the user.</summary>
public sealed class AppUpdateException : Exception
{
    public AppUpdateException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
