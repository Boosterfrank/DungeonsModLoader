namespace DungeonsModLoader.Core.Install;

/// <summary>
/// The .pak / .ucas / .utoc files that share one base name inside one folder of a package (an Unreal IoStore
/// set). A complete set has all three; a lone .pak is also valid (older pak-only mods). Paths are relative to the
/// package root and use '/' separators.
/// </summary>
public sealed record ModFileSet(string RelativeDirectory, string BaseName, string? PakFile, string? UcasFile, string? UtocFile)
{
    public bool IsComplete => PakFile is not null && UcasFile is not null && UtocFile is not null;

    public bool IsPakOnly => PakFile is not null && UcasFile is null && UtocFile is null;

    /// <summary>True for a complete set or a lone .pak; anything else is an incomplete set the game may reject.</summary>
    public bool IsValid => IsComplete || IsPakOnly;

    /// <summary>The files of the set that exist, as package-relative paths.</summary>
    public IEnumerable<string> Files
    {
        get
        {
            if (PakFile is not null) yield return PakFile;
            if (UcasFile is not null) yield return UcasFile;
            if (UtocFile is not null) yield return UtocFile;
        }
    }

    /// <summary>Base name without the Unreal "_P" patch suffix, for display.</summary>
    public string DisplayName => BaseName.EndsWith("_P", StringComparison.OrdinalIgnoreCase) ? BaseName[..^2] : BaseName;

    /// <summary>"Option A/MyMod_P" for a set inside a folder, "MyMod_P" at the root.</summary>
    public string DisplayPath => RelativeDirectory.Length == 0 ? BaseName : RelativeDirectory + "/" + BaseName;

    /// <summary>Explains why the set is incomplete, or null when it is valid.</summary>
    public string? Warning
    {
        get
        {
            if (IsValid)
            {
                return null;
            }

            var missing = new List<string>();
            if (PakFile is null) missing.Add(".pak");
            if (UcasFile is null) missing.Add(".ucas");
            if (UtocFile is null) missing.Add(".utoc");
            return $"'{DisplayPath}' is missing its {string.Join(" and ", missing)} file; the game may not load it.";
        }
    }
}
