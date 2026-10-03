namespace DungeonsModLoader.Core;

/// <summary>
/// Well-known application data locations under %LOCALAPPDATA%\DungeonsModLoader\.
/// Pure path computation; nothing here touches the disk except <see cref="EnsureCreated"/>.
/// </summary>
public sealed class AppPaths
{
    public AppPaths()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppInfo.FolderName))
    {
    }

    /// <summary>Creates paths rooted at a custom data folder (used by tests).</summary>
    public AppPaths(string root)
    {
        Root = root;
    }

    public string Root { get; }
    public string SettingsFile => Path.Combine(Root, "settings.json");
    public string ManifestFile => Path.Combine(Root, "manifest.json");
    public string ProfilesDirectory => Path.Combine(Root, "profiles");
    public string CacheDirectory => Path.Combine(Root, "cache");
    public string DownloadsDirectory => Path.Combine(Root, "downloads");
    public string LogsDirectory => Path.Combine(Root, "logs");
    public string BackupsDirectory => Path.Combine(Root, "backups");
    public string TempDirectory => Path.Combine(Root, "temp");

    /// <summary>Creates every directory this app writes to. Safe to call repeatedly.</summary>
    public void EnsureCreated()
    {
        foreach (var dir in new[] { Root, ProfilesDirectory, CacheDirectory, DownloadsDirectory, LogsDirectory, BackupsDirectory, TempDirectory })
        {
            Directory.CreateDirectory(dir);
        }
    }
}
