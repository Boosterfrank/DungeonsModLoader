using System.Text.RegularExpressions;

namespace DungeonsModLoader.Nexus;

/// <summary>
/// Keeps credentials out of the log: the per-user download token of <c>nxm://</c> links (<c>key=...</c>) and API
/// keys wherever they might appear in free text. Every log call that handles a URL, an argument list or a header
/// value goes through <see cref="Mask"/> first.
/// </summary>
public static partial class SecretMasker
{
    private const string Replacement = "***";

    /// <summary>Masks <c>key=</c> / <c>apikey=</c> query values and <c>apikey: value</c> header text.</summary>
    public static string Mask(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var masked = QueryKey().Replace(text, "$1" + Replacement);
        masked = HeaderKey().Replace(masked, "$1" + Replacement);
        return masked;
    }

    /// <summary>Masks every string of an argument list (for the startup log line).</summary>
    public static string[] Mask(IEnumerable<string>? values) =>
        values is null ? Array.Empty<string>() : values.Select(v => Mask(v)).ToArray();

    /// <summary>A short, non-reversible description of an API key for the log ("…a1b2", 40 chars).</summary>
    public static string Describe(string? apiKey)
    {
        if (string.IsNullOrEmpty(apiKey))
        {
            return "(none)";
        }

        var tail = apiKey.Length >= 4 ? apiKey[^4..] : apiKey;
        return $"…{tail} ({apiKey.Length} chars)";
    }

    [GeneratedRegex(@"(?i)\b((?:api)?key=)[^&\s""'<>]+")]
    private static partial Regex QueryKey();

    [GeneratedRegex(@"(?i)(apikey[""']?\s*[:=]\s*[""']?)[A-Za-z0-9+/=_\-]{8,}")]
    private static partial Regex HeaderKey();
}
