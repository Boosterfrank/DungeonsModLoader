using DungeonsModLoader.Core.Mods;
using DungeonsModLoader.Nexus.Api;

namespace DungeonsModLoader.Nexus.Linking;

/// <summary>How sure the matcher is that a Nexus mod is the local mod's page.</summary>
public enum ModLinkConfidence
{
    /// <summary>Some words in common only; never preselected.</summary>
    Low,

    /// <summary>The local name is part of the mod's name (or most words match); preselected for the user to confirm.</summary>
    Medium,

    /// <summary>Same name.</summary>
    High,
}

/// <summary>A Nexus mod that may be a local mod's page, with its fit (<see cref="ModNameMatcher.Score"/>).</summary>
public sealed record ModLinkCandidate(NexusMod Mod, double Score)
{
    public const double MediumThreshold = 0.6;
    public const double HighThreshold = 0.9;

    public ModLinkConfidence Confidence => Score >= HighThreshold ? ModLinkConfidence.High : Score >= MediumThreshold ? ModLinkConfidence.Medium : ModLinkConfidence.Low;

    public int ScorePercent => (int)Math.Round(Score * 100);
}

/// <summary>The Nexus candidates found for one local mod, best first.</summary>
/// <param name="Error">User-facing text when Nexus could not be searched at all; null when the search ran.</param>
public sealed record ModLinkSuggestion(ModInfo Mod, IReadOnlyList<ModLinkCandidate> Candidates, string? Error = null)
{
    /// <summary>The candidate to preselect: the best one unless it is a weak fit.</summary>
    public ModLinkCandidate? Best => Candidates.FirstOrDefault(c => c.Confidence != ModLinkConfidence.Low);
}
