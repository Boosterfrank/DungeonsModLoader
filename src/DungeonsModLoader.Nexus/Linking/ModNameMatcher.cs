using System.Text;
using DungeonsModLoader.Nexus.Api;

namespace DungeonsModLoader.Nexus.Linking;

/// <summary>
/// Pure text matching between the names a hand-installed mod carries (display name, folder name, pak file names
/// such as <c>BlueprintLoader_P</c>) and the names of mods on Nexus Mods: which texts to search for, and how well a
/// search hit fits. Scores are 0..1; see <see cref="ModLinkCandidate"/> for the confidence bands.
/// </summary>
public static class ModNameMatcher
{
    /// <summary>Words too common in this game's mod names to search for on their own.</summary>
    private static readonly HashSet<string> Noise = new(StringComparer.OrdinalIgnoreCase)
    {
        "mod", "mods", "the", "and", "for", "with", "pak", "minecraft", "dungeons", "mcd", "mcd2", "md2", "version", "fix", "new",
    };

    /// <summary>Lower-case letters and digits only: "Blueprint Loader" and "BlueprintLoader_P" both become "blueprintloader".</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var trimmed = text.Trim();
        // A pak set's base name ends in "_P"; drop that so it matches the mod's name.
        if (trimmed.EndsWith("_P", StringComparison.OrdinalIgnoreCase) && trimmed.Length > 2)
        {
            trimmed = trimmed[..^2];
        }

        var builder = new StringBuilder(trimmed.Length);
        foreach (var c in trimmed)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Splits a name into words: at spaces and punctuation, and inside camel case ("BlueprintLoader" gives
    /// "Blueprint", "Loader"; "D1Stats" gives "D1", "Stats"; "DungeonsGUIX" gives "Dungeons", "GUIX"). Single
    /// characters (the "_P" suffix, stray letters) are dropped.
    /// </summary>
    public static IReadOnlyList<string> Words(string? text)
    {
        var words = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return words;
        }

        var current = new StringBuilder();
        void Flush()
        {
            if (current.Length > 1)
            {
                words.Add(current.ToString());
            }

            current.Clear();
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (!char.IsLetterOrDigit(c))
            {
                Flush();
                continue;
            }

            if (current.Length > 0)
            {
                var previous = current[^1];
                var boundary =
                    (char.IsLower(previous) && char.IsUpper(c))                                   // blueprint|Loader
                    || (char.IsDigit(previous) && char.IsLetter(c))                                // D1|Stats
                    || (char.IsUpper(previous) && char.IsUpper(c) && i + 1 < text.Length && char.IsLower(text[i + 1])); // GUI|Xmenu
                if (boundary)
                {
                    Flush();
                }
            }

            current.Append(c);
        }

        Flush();
        return words;
    }

    /// <summary>
    /// The texts to search Nexus for, most specific first: each name as spaced words ("Blueprint Loader"), then,
    /// when a name has several words, its distinctive single words ("Blueprint") as a fallback. At most five.
    /// </summary>
    public static IReadOnlyList<string> SearchQueries(IEnumerable<string?> names)
    {
        var phrases = new List<string>();
        var singles = new List<string>();
        foreach (var name in names)
        {
            var words = Words(name);
            if (words.Count == 0)
            {
                continue;
            }

            phrases.Add(string.Join(' ', words));
            if (words.Count > 1)
            {
                singles.AddRange(words.Where(w => w.Length >= 4 && !Noise.Contains(w)));
            }
        }

        return phrases.Concat(singles)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToList();
    }

    /// <summary>
    /// How well a Nexus mod fits the local names (0..1). Equal normalized names score 1; one containing the other
    /// scores by how much of the longer name is covered; otherwise shared words count (what share of the local words
    /// appear in the mod's name weighs most). Downloads add up to 0.05 so, between near-equal fits, the original
    /// outranks its translations and add-ons.
    /// </summary>
    public static double Score(IEnumerable<string?> localNames, NexusMod candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var candidateNormalized = Normalize(candidate.Name);
        if (candidateNormalized.Length == 0)
        {
            return 0;
        }

        var candidateWords = Words(candidate.Name).Select(w => w.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        var best = 0.0;
        foreach (var name in localNames)
        {
            var normalized = Normalize(name);
            if (normalized.Length == 0)
            {
                continue;
            }

            var score = 0.0;
            if (normalized == candidateNormalized)
            {
                score = 1.0;
            }
            else if (candidateNormalized.Contains(normalized, StringComparison.Ordinal) || normalized.Contains(candidateNormalized, StringComparison.Ordinal))
            {
                var ratio = (double)Math.Min(normalized.Length, candidateNormalized.Length) / Math.Max(normalized.Length, candidateNormalized.Length);
                score = 0.6 + 0.3 * ratio;
            }

            var words = Words(name).Select(w => w.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
            if (words.Count > 0 && candidateWords.Count > 0)
            {
                var common = words.Count(candidateWords.Contains);
                if (common > 0)
                {
                    var coverage = (double)common / words.Count;
                    var precision = (double)common / candidateWords.Count;
                    score = Math.Max(score, 0.55 * coverage + 0.35 * precision);
                }
            }

            best = Math.Max(best, score);
        }

        if (best <= 0)
        {
            return 0;
        }

        var popularity = Math.Min(1.0, Math.Log10(Math.Max(0, candidate.Downloads) + 1) / 5.0) * 0.05;
        return Math.Min(1.0, best + popularity);
    }
}
