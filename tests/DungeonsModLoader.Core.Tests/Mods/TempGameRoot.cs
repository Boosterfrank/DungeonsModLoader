using DungeonsModLoader.Core.Game;

namespace DungeonsModLoader.Core.Tests.Mods;

/// <summary>
/// A throw-away game installation under the temp folder: <c>&lt;temp&gt;/dml-tests/&lt;guid&gt;/game/Dungeons/Content/Paks/~mods</c>
/// plus an isolated app-data folder for the manifest. Deleted on dispose.
/// </summary>
internal sealed class TempGameRoot : IDisposable
{
    public TempGameRoot()
    {
        BaseDirectory = Path.Combine(Path.GetTempPath(), "dml-tests", Guid.NewGuid().ToString("N"));
        Root = Path.Combine(BaseDirectory, "game");
        DataDirectory = Path.Combine(BaseDirectory, "data");
        Directory.CreateDirectory(ModsDirectory);
        Directory.CreateDirectory(DataDirectory);
        Installation = new GameInstallation { Root = Root, Source = GameSource.Manual };
        Paths = new AppPaths(DataDirectory);
    }

    public string BaseDirectory { get; }

    public string Root { get; }

    public string DataDirectory { get; }

    public GameInstallation Installation { get; }

    public AppPaths Paths { get; }

    public string PaksDirectory => Path.Combine(Root, "Dungeons", "Content", "Paks");

    public string ModsDirectory => Path.Combine(PaksDirectory, "~mods");

    public string DisabledModsDirectory => Path.Combine(Root, "Dungeons", AppInfo.FolderName + "_Disabled");

    public string EnabledPath(string folderName) => Path.Combine(ModsDirectory, folderName);

    public string DisabledPath(string folderName) => Path.Combine(DisabledModsDirectory, folderName);

    /// <summary>Creates a mod folder with a dummy pak set (<c>Name_P.pak/.ucas/.utoc</c>) in the enabled or disabled location.</summary>
    public string CreateMod(string folderName, bool enabled = true)
    {
        var path = enabled ? EnabledPath(folderName) : DisabledPath(folderName);
        Directory.CreateDirectory(path);
        foreach (var extension in new[] { ".pak", ".ucas", ".utoc" })
        {
            File.WriteAllText(Path.Combine(path, folderName + "_P" + extension), folderName + extension);
        }

        return path;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(BaseDirectory))
            {
                Directory.Delete(BaseDirectory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: temp folders are cleaned up by the OS eventually.
        }
    }
}
