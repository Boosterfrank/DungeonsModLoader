using System.Security;

namespace DungeonsModLoader.Core.Game;

/// <summary>
/// Path normalization shared by the game locators: a game root is compared case-insensitively, as a full path,
/// with forward slashes converted and trailing separators removed (<c>c:/games/x/</c> == <c>C:\Games\X</c>).
/// </summary>
public static class GamePaths
{
    /// <summary>
    /// Normalizes <paramref name="path"/> for comparison and storage, or returns <c>null</c> when it is empty or not
    /// a usable path. Never throws.
    /// </summary>
    public static string? TryNormalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var full = Path.GetFullPath(path.Trim().Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar));
            var trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            // Keep the separator on a bare drive root ("C:\"), where trimming would change the meaning.
            if (trimmed.Length == 0 || trimmed.EndsWith(Path.VolumeSeparatorChar))
            {
                return full;
            }

            return trimmed;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or SecurityException)
        {
            return null;
        }
    }

    /// <summary>True when both paths normalize to the same folder (case-insensitive). Two unusable paths are not equal.</summary>
    public static bool AreSameFolder(string? a, string? b)
    {
        var left = TryNormalize(a);
        var right = TryNormalize(b);
        return left is not null && right is not null && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }
}
