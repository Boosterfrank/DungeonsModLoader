using System.Text;
using System.Text.RegularExpressions;

namespace DungeonsModLoader.Core.Install;

/// <summary>Turns a display name into a safe single folder name under <c>~mods</c>.</summary>
public static partial class FolderNameSanitizer
{
    private const int MaxLength = 80;

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Replaces invalid path characters, collapses whitespace, trims trailing dots/spaces, avoids reserved device
    /// names and caps the length. Returns <paramref name="fallback"/> when nothing usable remains.
    /// </summary>
    public static string Sanitize(string? name, string fallback = "Mod")
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return fallback;
        }

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            builder.Append(invalid.Contains(ch) || ch is '/' or '\\' || char.IsControl(ch) ? ' ' : ch);
        }

        var result = WhitespaceRun().Replace(builder.ToString(), " ").Trim().TrimEnd('.', ' ');
        if (result.Length > MaxLength)
        {
            result = result[..MaxLength].TrimEnd('.', ' ');
        }

        if (result.Length == 0 || result is "." or ".." || ReservedNames.Contains(result))
        {
            return fallback;
        }

        return result;
    }

    /// <summary>Appends " (2)", " (3)", ... until <paramref name="exists"/> returns false.</summary>
    public static string MakeUnique(string baseName, Func<string, bool> exists)
    {
        var candidate = baseName;
        for (var n = 2; exists(candidate); n++)
        {
            candidate = $"{baseName} ({n})";
        }

        return candidate;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRun();
}
