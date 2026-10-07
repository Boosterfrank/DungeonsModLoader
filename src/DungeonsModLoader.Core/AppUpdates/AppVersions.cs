namespace DungeonsModLoader.Core.AppUpdates;

/// <summary>Version parsing for release tags ("v0.2.0", "0.2.0-beta", "1.0") and the app's own version string.</summary>
public static class AppVersions
{
    /// <summary>
    /// Parses a version. A leading "v" is ignored, as is anything from the first '-' or '+' on ("0.2.0-beta.1",
    /// "0.2.0+sha"). Missing components count as 0, so "1.0" equals "1.0.0".
    /// </summary>
    public static bool TryParse(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var span = text.Trim();
        if (span.StartsWith('v') || span.StartsWith('V'))
        {
            span = span[1..];
        }

        var cut = span.IndexOfAny(['-', '+', ' ']);
        if (cut >= 0)
        {
            span = span[..cut];
        }

        var parts = span.Split('.');
        if (parts.Length is 0 or > 4)
        {
            return false;
        }

        var numbers = new int[4];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out numbers[i]))
            {
                return false;
            }
        }

        version = parts.Length == 4 ? new Version(numbers[0], numbers[1], numbers[2], numbers[3]) : new Version(numbers[0], numbers[1], numbers[2]);
        return true;
    }

    /// <summary>True when <paramref name="candidate"/> parses and is strictly newer than <paramref name="current"/>.</summary>
    public static bool IsNewer(string? candidate, string? current)
    {
        if (!TryParse(candidate, out var candidateVersion) || !TryParse(current, out var currentVersion))
        {
            return false;
        }

        return Normalize(candidateVersion) > Normalize(currentVersion);
    }

    /// <summary>"0.2.0" for display (three components; a fourth only when it is not zero).</summary>
    public static string ToText(Version version) =>
        version.Revision > 0 ? version.ToString(4) : new Version(version.Major, Math.Max(0, version.Minor), Math.Max(0, version.Build)).ToString(3);

    private static Version Normalize(Version version) =>
        new(version.Major, Math.Max(0, version.Minor), Math.Max(0, version.Build), Math.Max(0, version.Revision));
}
