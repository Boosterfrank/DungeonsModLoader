namespace DungeonsModLoader.Core.Install;

public enum InstallSourceKind
{
    /// <summary>A .zip / .7z / .rar archive.</summary>
    Archive,

    /// <summary>Loose .pak / .ucas / .utoc files dropped together.</summary>
    LooseFiles,

    /// <summary>A folder on disk (its content is treated like an extracted archive).</summary>
    Folder,
}

/// <summary>What the user asked to install: an archive, loose mod files, or a folder.</summary>
public sealed record InstallSource(InstallSourceKind Kind, IReadOnlyList<string> Paths)
{
    public static readonly string[] ArchiveExtensions = [".zip", ".7z", ".rar"];
    public static readonly string[] ModFileExtensions = [".pak", ".ucas", ".utoc"];

    public static InstallSource FromArchive(string path) => new(InstallSourceKind.Archive, [path]);

    public static InstallSource FromLooseFiles(IEnumerable<string> paths) => new(InstallSourceKind.LooseFiles, paths.ToList());

    public static InstallSource FromFolder(string path) => new(InstallSourceKind.Folder, [path]);

    /// <summary>Human-readable name of the source (archive or folder name, or the first file's base name).</summary>
    public string DisplayName => Kind switch
    {
        InstallSourceKind.Folder => Path.GetFileName(Path.TrimEndingDirectorySeparator(Paths[0])),
        InstallSourceKind.Archive => Path.GetFileNameWithoutExtension(Paths[0]),
        _ => Path.GetFileNameWithoutExtension(Paths[0]),
    };

    public static bool IsArchive(string path) =>
        ArchiveExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static bool IsModFile(string path) =>
        ModFileExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Turns a set of dropped/picked paths into install sources: every archive and every folder becomes its own
    /// source; all loose mod files together form one source. Anything else is returned in <paramref name="ignored"/>.
    /// </summary>
    public static IReadOnlyList<InstallSource> FromPaths(IEnumerable<string> paths, out IReadOnlyList<string> ignored)
    {
        var sources = new List<InstallSource>();
        var loose = new List<string>();
        var skipped = new List<string>();

        foreach (var path in paths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Directory.Exists(path))
            {
                sources.Add(FromFolder(path));
            }
            else if (IsArchive(path))
            {
                sources.Add(FromArchive(path));
            }
            else if (IsModFile(path))
            {
                loose.Add(path);
            }
            else
            {
                skipped.Add(path);
            }
        }

        if (loose.Count > 0)
        {
            sources.Add(FromLooseFiles(loose));
        }

        ignored = skipped;
        return sources;
    }
}
