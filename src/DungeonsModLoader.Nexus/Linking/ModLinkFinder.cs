using DungeonsModLoader.Core.Mods;
using DungeonsModLoader.Nexus.Api;
using Microsoft.Extensions.Logging;

namespace DungeonsModLoader.Nexus.Linking;

/// <summary>Finds the Nexus Mods page of a mod that was installed by hand, from the names it carries.</summary>
public interface IModLinkFinder
{
    /// <summary>
    /// Searches Nexus Mods for the mod's display name, folder name and pak file names (public GraphQL search, no
    /// account needed) and returns the hits scored and sorted, best first. Never throws for Nexus errors: a search
    /// that fails is reported in <see cref="ModLinkSuggestion.Error"/>.
    /// </summary>
    Task<ModLinkSuggestion> FindAsync(ModInfo mod, CancellationToken cancellationToken = default);

    /// <summary>Searches with a text the user typed; the hits are scored against the mod's own names as usual.</summary>
    Task<ModLinkSuggestion> SearchAsync(ModInfo mod, string query, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IModLinkFinder"/>
public sealed class ModLinkFinder : IModLinkFinder
{
    /// <summary>Candidates kept per mod (the dropdown stays readable).</summary>
    public const int MaxCandidates = 8;

    /// <summary>Hits per search request.</summary>
    private const int PageSize = 12;

    /// <summary>A hit this good ends the search early (the remaining queries would only add noise).</summary>
    private const double GoodEnough = 0.95;

    private readonly INexusApiClient _client;
    private readonly ILogger<ModLinkFinder> _logger;

    public ModLinkFinder(INexusApiClient client, ILogger<ModLinkFinder> logger)
    {
        _client = client;
        _logger = logger;
    }

    /// <summary>The names a local mod carries: display name, folder name and the base names of its pak files.</summary>
    public static IReadOnlyList<string> NamesOf(ModEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var names = new List<string> { entry.DisplayName, entry.FolderName };
        foreach (var file in entry.Files)
        {
            if (file.RelativePath.EndsWith(".pak", StringComparison.OrdinalIgnoreCase))
            {
                var baseName = Path.GetFileNameWithoutExtension(file.RelativePath);
                if (baseName.EndsWith("_P", StringComparison.OrdinalIgnoreCase))
                {
                    baseName = baseName[..^2];
                }

                names.Add(baseName);
            }
        }

        return names.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<ModLinkSuggestion> FindAsync(ModInfo mod, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mod);
        var names = NamesOf(mod.Entry);
        var queries = ModNameMatcher.SearchQueries(names);
        _logger.LogDebug("Looking for the Nexus page of '{Mod}' with {Count} search text(s): {Queries}", mod.Entry.DisplayName, queries.Count, string.Join(" | ", queries));

        var found = new Dictionary<long, ModLinkCandidate>();
        string? error = null;
        foreach (var query in queries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var page = await _client.SearchAsync(query, 0, PageSize, cancellationToken: cancellationToken).ConfigureAwait(false);
                Merge(found, names, page.Items);
            }
            catch (NexusException ex)
            {
                _logger.LogWarning("Search for '{Query}' failed while linking '{Mod}': {Message}", query, mod.Entry.DisplayName, ex.Message);
                error = ex.Message;
            }

            if (found.Values.Any(c => c.Score >= GoodEnough))
            {
                break;
            }
        }

        var suggestion = new ModLinkSuggestion(mod, Rank(found), found.Count == 0 ? error : null);
        _logger.LogInformation(
            "Nexus candidates for '{Mod}': {Count} ({Best})",
            mod.Entry.DisplayName,
            suggestion.Candidates.Count,
            suggestion.Candidates.Count == 0 ? "none" : $"best '{suggestion.Candidates[0].Mod.Name}' at {suggestion.Candidates[0].ScorePercent}%");
        return suggestion;
    }

    public async Task<ModLinkSuggestion> SearchAsync(ModInfo mod, string query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mod);
        var text = (query ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return await FindAsync(mod, cancellationToken).ConfigureAwait(false);
        }

        var names = NamesOf(mod.Entry);
        var found = new Dictionary<long, ModLinkCandidate>();
        try
        {
            var page = await _client.SearchAsync(text, 0, PageSize, refresh: false, cancellationToken).ConfigureAwait(false);
            Merge(found, names, page.Items);
            // A hand-typed search names the mod the user means: keep every hit, even the ones that share no words.
            return new ModLinkSuggestion(mod, Rank(found, keepAll: true));
        }
        catch (NexusException ex)
        {
            _logger.LogWarning("Search for '{Query}' failed: {Message}", text, ex.Message);
            return new ModLinkSuggestion(mod, Array.Empty<ModLinkCandidate>(), ex.Message);
        }
    }

    private static void Merge(Dictionary<long, ModLinkCandidate> found, IReadOnlyList<string> names, IEnumerable<NexusMod> hits)
    {
        foreach (var hit in hits)
        {
            var score = ModNameMatcher.Score(names, hit);
            if (!found.TryGetValue(hit.ModId, out var existing) || existing.Score < score)
            {
                found[hit.ModId] = new ModLinkCandidate(hit, score);
            }
        }
    }

    private static IReadOnlyList<ModLinkCandidate> Rank(Dictionary<long, ModLinkCandidate> found, bool keepAll = false) =>
        found.Values
            .Where(c => keepAll || c.Score > 0)
            .OrderByDescending(c => c.Score)
            .ThenByDescending(c => c.Mod.Downloads)
            .Take(MaxCandidates)
            .ToList();
}
