using System.Text.RegularExpressions;

namespace DungeonsModLoader.Core.Install;

/// <summary>Derives a readable mod name (and version, when present) from archive, folder and file names.</summary>
public static partial class ModNameHeuristics
{
    /// <summary>
    /// Nexus Mods download names: "Second Skin Layer-10-1-2-0-1696000000" = name, mod id, version parts, timestamp.
    /// </summary>
    [GeneratedRegex(@"^(?<name>.+?)-(?<id>\d+)-(?<ver>\d+(?:-\d+)*)-(?<ts>\d{9,})$")]
    private static partial Regex NexusPattern();

    /// <summary>Trailing versions such as " v1.2", " v2", "_1.0.3", "-2.0" (a bare number counts only with a 'v' prefix or a dot).</summary>
    [GeneratedRegex(@"[\s_\-]+(?:v(?<ver>\d+(?:\.\d+)*)|(?<ver>\d+(?:\.\d+)+))$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingVersionPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRun();

    /// <summary>Name and version from an archive file name (without extension) or a folder name.</summary>
    public static (string Name, string? Version) FromArchiveName(string? fileNameWithoutExtension)
    {
        var text = (fileNameWithoutExtension ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return ("Mod", null);
        }

        var nexus = NexusPattern().Match(text);
        if (nexus.Success)
        {
            return (Clean(nexus.Groups["name"].Value), nexus.Groups["ver"].Value.Replace('-', '.'));
        }

        var trailing = TrailingVersionPattern().Match(text);
        if (trailing.Success && trailing.Index > 0)
        {
            return (Clean(text[..trailing.Index]), trailing.Groups["ver"].Value);
        }

        return (Clean(text), null);
    }

    /// <summary>Name from a file set: base name without the "_P" suffix, underscores as spaces.</summary>
    public static string FromFileSet(ModFileSet set) => Clean(set.DisplayName);

    private static string Clean(string value)
    {
        var cleaned = WhitespaceRun().Replace(value.Replace('_', ' '), " ").Trim(' ', '-', '.');
        return cleaned.Length == 0 ? "Mod" : cleaned;
    }
}
