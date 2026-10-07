using DungeonsModLoader.Nexus.Text;

namespace DungeonsModLoader.Nexus.Api;

/// <summary>
/// The pictures the official API gives for a mod: the page's header picture plus every <c>[img]</c> in the
/// description (the website's Images tab has no public API). Deduplicated, http(s) only, in page order.
/// </summary>
public static class ModImageList
{
    public static IReadOnlyList<string> Collect(string? pictureUrl, string? description)
    {
        var urls = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            var trimmed = url.Trim();
            if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                && seen.Add(uri.AbsoluteUri))
            {
                urls.Add(uri.AbsoluteUri);
            }
        }

        Add(pictureUrl);
        foreach (var node in BbCode.Parse(description))
        {
            if (node is BbImage image)
            {
                Add(image.Url);
            }
        }

        return urls;
    }
}
