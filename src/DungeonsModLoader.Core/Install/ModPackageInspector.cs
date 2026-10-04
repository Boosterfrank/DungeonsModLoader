namespace DungeonsModLoader.Core.Install;

/// <summary>What <see cref="ModPackageInspector.Inspect"/> found in a staged package.</summary>
/// <param name="Root">Effective package root (wrapper folders stripped). Relative paths below are relative to it.</param>
public sealed record PackageContents(
    string Root,
    IReadOnlyList<InstallCandidate> Candidates,
    IReadOnlyList<string> ExtraFiles,
    IReadOnlyList<string> DocumentationFiles,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Finds the mod file sets in an extracted package: groups .pak/.ucas/.utoc by folder and base name, strips
/// single-folder wrappers, separates documentation from real data files and reports incomplete sets.
/// </summary>
public static class ModPackageInspector
{
    private static readonly HashSet<string> DocumentationExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".nfo", ".url", ".pdf", ".html", ".htm", ".rtf",
    };

    private static readonly HashSet<string> JunkFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "thumbs.db", "desktop.ini", ".ds_store",
    };

    private static readonly HashSet<string> JunkDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "__MACOSX",
    };

    public static PackageContents Inspect(string stagingRoot)
    {
        var root = StripWrappers(Path.GetFullPath(stagingRoot));
        var warnings = new List<string>();
        var docs = new List<string>();
        var extras = new List<string>();
        var builders = new Dictionary<(string Dir, string Base), SetBuilder>();
        var order = new List<(string Dir, string Base)>();

        foreach (var file in EnumerateFiles(root))
        {
            var relative = ToRelative(root, file);
            var name = Path.GetFileName(relative);
            if (JunkFileNames.Contains(name))
            {
                continue;
            }

            var extension = Path.GetExtension(relative);
            if (InstallSource.ModFileExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                var directory = GetDirectory(relative);
                var baseName = Path.GetFileNameWithoutExtension(relative);
                var key = (directory.ToLowerInvariant(), baseName.ToLowerInvariant());
                if (!builders.TryGetValue(key, out var builder))
                {
                    builder = new SetBuilder(directory, baseName);
                    builders[key] = builder;
                    order.Add(key);
                }

                if (!builder.TrySet(extension, relative))
                {
                    warnings.Add($"'{relative}' duplicates another file of the same set and was ignored.");
                }
            }
            else if (DocumentationExtensions.Contains(extension))
            {
                docs.Add(relative);
            }
            else
            {
                extras.Add(relative);
            }
        }

        var sets = order.Select(key => builders[key].Build()).ToList();
        warnings.AddRange(sets.Where(set => !set.IsValid).Select(set => set.Warning!));

        var candidates = sets
            .GroupBy(set => set.RelativeDirectory, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key.Length == 0 ? 0 : 1)
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new InstallCandidate(
                group.Key,
                group.Key.Length == 0 ? "Main" : group.Key,
                group.ToList(),
                IsDefault: false))
            .ToList();

        // Default: the root folder when it has a valid set, otherwise the first folder that has one.
        var defaultIndex = candidates.FindIndex(c => c.RelativeDirectory.Length == 0 && c.FileSets.Any(s => s.IsValid));
        if (defaultIndex < 0)
        {
            defaultIndex = candidates.FindIndex(c => c.FileSets.Any(s => s.IsValid));
        }

        if (defaultIndex >= 0)
        {
            candidates[defaultIndex] = candidates[defaultIndex] with { IsDefault = true };
        }

        return new PackageContents(root, candidates, extras, docs, warnings);
    }

    /// <summary>
    /// Descends through folders that contain nothing but a single sub folder (archives wrapped in "ModName/",
    /// "Dungeons/Content/Paks/~mods/ModName/", ...). Junk folders such as __MACOSX are ignored.
    /// </summary>
    public static string StripWrappers(string root)
    {
        var current = root;
        for (var depth = 0; depth < 16; depth++)
        {
            string[] directories;
            bool hasFiles;
            try
            {
                directories = Directory.GetDirectories(current)
                    .Where(dir => !JunkDirectoryNames.Contains(Path.GetFileName(dir)))
                    .ToArray();
                hasFiles = Directory.EnumerateFiles(current).Any(file => !JunkFileNames.Contains(Path.GetFileName(file)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                break;
            }

            if (hasFiles || directories.Length != 1)
            {
                break;
            }

            current = directories[0];
        }

        return current;
    }

    /// <summary>Package-relative path with '/' separators.</summary>
    public static string ToRelative(string root, string fullPath) =>
        Path.GetRelativePath(root, fullPath).Replace('\\', '/');

    /// <summary>Folder part of a package-relative path ('' for the root).</summary>
    public static string GetDirectory(string relativePath)
    {
        var slash = relativePath.LastIndexOf('/');
        return slash < 0 ? string.Empty : relativePath[..slash];
    }

    private static IEnumerable<string> EnumerateFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            IEnumerable<string> files;
            IEnumerable<string> subDirectories;
            try
            {
                files = Directory.EnumerateFiles(directory).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
                subDirectories = Directory.EnumerateDirectories(directory)
                    .Where(dir => !JunkDirectoryNames.Contains(Path.GetFileName(dir)))
                    .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                yield return file;
            }

            foreach (var subDirectory in subDirectories)
            {
                pending.Push(subDirectory);
            }
        }
    }

    private sealed class SetBuilder
    {
        private readonly string _directory;
        private readonly string _baseName;
        private string? _pak;
        private string? _ucas;
        private string? _utoc;

        public SetBuilder(string directory, string baseName)
        {
            _directory = directory;
            _baseName = baseName;
        }

        public bool TrySet(string extension, string relativePath)
        {
            switch (extension.ToLowerInvariant())
            {
                case ".pak" when _pak is null:
                    _pak = relativePath;
                    return true;
                case ".ucas" when _ucas is null:
                    _ucas = relativePath;
                    return true;
                case ".utoc" when _utoc is null:
                    _utoc = relativePath;
                    return true;
                default:
                    return false;
            }
        }

        public ModFileSet Build() => new(_directory, _baseName, _pak, _ucas, _utoc);
    }
}
