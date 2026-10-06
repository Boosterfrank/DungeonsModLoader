using System.Globalization;

namespace DungeonsModLoader.Nexus.Nxm;

/// <summary>
/// A parsed <c>nxm://{game}/mods/{mod_id}/files/{file_id}?key=…&amp;expires=…&amp;user_id=…</c> link, as emitted
/// by the website's "Mod Manager Download" button. <see cref="Key"/> and <see cref="Expires"/> are the per-user
/// download token free accounts must pass to <c>download_link.json</c>; they are secrets and never logged.
/// </summary>
public sealed record NxmLink(string GameDomain, long ModId, long FileId, string? Key, long? Expires, long? UserId)
{
    public const string Scheme = "nxm";

    /// <summary>True when the link carries the website token (free accounts); premium links may omit it.</summary>
    public bool HasToken => !string.IsNullOrEmpty(Key) && Expires is not null;

    /// <summary>True when the token has already expired according to the local clock.</summary>
    public bool IsExpired => Expires is { } expires && DateTimeOffset.FromUnixTimeSeconds(expires) <= DateTimeOffset.UtcNow;

    /// <summary>True when the link targets <see cref="NexusConstants.GameDomain"/> (case-insensitive).</summary>
    public bool IsForThisGame => string.Equals(GameDomain, NexusConstants.GameDomain, StringComparison.OrdinalIgnoreCase);

    /// <summary>The link without its token, safe for the log.</summary>
    public override string ToString() => $"nxm://{GameDomain}/mods/{ModId}/files/{FileId}{(HasToken ? "?key=***&expires=***" : string.Empty)}";

    /// <summary>True when <paramref name="text"/> looks like any nxm link (also the ones this app does not handle, e.g. collections).</summary>
    public static bool IsNxmUri(string? text) =>
        !string.IsNullOrWhiteSpace(text) && text.TrimStart().StartsWith(Scheme + ":", StringComparison.OrdinalIgnoreCase);

    public static bool TryParse(string? text, out NxmLink? link)
    {
        link = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) || !string.Equals(uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 4
            || !string.Equals(segments[0], "mods", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(segments[2], "files", StringComparison.OrdinalIgnoreCase)
            || !long.TryParse(segments[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var modId)
            || !long.TryParse(segments[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var fileId)
            || modId <= 0 || fileId <= 0)
        {
            return false;
        }

        var query = ParseQuery(uri.Query);
        query.TryGetValue("key", out var key);
        long? expires = query.TryGetValue("expires", out var expiresText) && long.TryParse(expiresText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var e) ? e : null;
        long? userId = query.TryGetValue("user_id", out var userText) && long.TryParse(userText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var u) ? u : null;

        link = new NxmLink(uri.Host, modId, fileId, string.IsNullOrEmpty(key) ? null : key, expires, userId);
        return true;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            var name = Uri.UnescapeDataString(separator < 0 ? part : part[..separator]);
            var value = separator < 0 ? string.Empty : Uri.UnescapeDataString(part[(separator + 1)..].Replace('+', ' '));
            result[name] = value;
        }

        return result;
    }
}
