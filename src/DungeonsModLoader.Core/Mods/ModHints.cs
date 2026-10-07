namespace DungeonsModLoader.Core.Mods;

/// <summary>
/// Another enabled mod ships files with the same base names as this one (for example both contain
/// <c>Hud_P.pak</c>). The game loads whichever it finds first, so one of the two usually does not work.
/// </summary>
/// <param name="Other">The other enabled mod.</param>
/// <param name="SharedBaseNames">The base names (file name without extension) both mods contain, sorted.</param>
public sealed record ModConflict(ModEntry Other, IReadOnlyList<string> SharedBaseNames);

/// <summary>Finds enabled mods that overwrite each other's files (the spec's "conflict hint"). Pure; no disk access.</summary>
public static class ModConflictDetector
{
    private static readonly HashSet<string> ModFileExtensions = new(StringComparer.OrdinalIgnoreCase) { ".pak", ".ucas", ".utoc" };

    /// <summary>
    /// Conflicts among the enabled mods in <paramref name="mods"/>, keyed by mod id. A mod without conflicts has no
    /// entry. Only <c>.pak/.ucas/.utoc</c> files count (a README with the same name is harmless); the three files of
    /// one set share a base name, so a set collides as a whole.
    /// </summary>
    public static IReadOnlyDictionary<Guid, IReadOnlyList<ModConflict>> Find(IEnumerable<ModInfo> mods)
    {
        ArgumentNullException.ThrowIfNull(mods);

        var owners = new Dictionary<string, List<ModEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in mods)
        {
            if (!mod.IsEnabled)
            {
                continue;
            }

            foreach (var baseName in BaseNames(mod.Entry))
            {
                if (!owners.TryGetValue(baseName, out var list))
                {
                    list = new List<ModEntry>();
                    owners[baseName] = list;
                }

                list.Add(mod.Entry);
            }
        }

        // mod id -> other mod id -> shared base names
        var shared = new Dictionary<Guid, Dictionary<Guid, (ModEntry Other, SortedSet<string> Names)>>();
        foreach (var (baseName, list) in owners)
        {
            if (list.Count < 2)
            {
                continue;
            }

            foreach (var mod in list)
            {
                foreach (var other in list)
                {
                    if (other.Id == mod.Id)
                    {
                        continue;
                    }

                    if (!shared.TryGetValue(mod.Id, out var perMod))
                    {
                        perMod = new Dictionary<Guid, (ModEntry, SortedSet<string>)>();
                        shared[mod.Id] = perMod;
                    }

                    if (!perMod.TryGetValue(other.Id, out var pair))
                    {
                        pair = (other, new SortedSet<string>(StringComparer.OrdinalIgnoreCase));
                        perMod[other.Id] = pair;
                    }

                    pair.Names.Add(baseName);
                }
            }
        }

        var result = new Dictionary<Guid, IReadOnlyList<ModConflict>>();
        foreach (var (modId, perMod) in shared)
        {
            result[modId] = perMod.Values
                .OrderBy(p => p.Other.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .Select(p => new ModConflict(p.Other, p.Names.ToList()))
                .ToList();
        }

        return result;
    }

    /// <summary>Base names (file name without extension, case preserved) of the mod files recorded for <paramref name="entry"/>.</summary>
    public static IReadOnlySet<string> BaseNames(ModEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in entry.Files)
        {
            var extension = Path.GetExtension(file.RelativePath);
            if (ModFileExtensions.Contains(extension))
            {
                names.Add(Path.GetFileNameWithoutExtension(file.RelativePath));
            }
        }

        return names;
    }
}

/// <summary>Whether a required mod is present and enabled.</summary>
public enum DependencyStatus
{
    /// <summary>Installed and enabled: nothing to do.</summary>
    Satisfied,

    /// <summary>Installed but disabled: enabling it fixes the hint.</summary>
    Disabled,

    /// <summary>Not installed at all.</summary>
    NotInstalled,
}

/// <summary>One requirement of an installed mod and whether it is met here.</summary>
/// <param name="InstalledMod">The installed copy of the requirement, when there is one.</param>
public sealed record ModDependencyHint(ModRequirementRecord Requirement, DependencyStatus Status, ModEntry? InstalledMod)
{
    public bool IsSatisfied => Status == DependencyStatus.Satisfied;
}

/// <summary>Checks a mod's recorded requirements against the installed mods (the spec's "dependency hint"). Pure.</summary>
public static class ModDependencyHints
{
    /// <summary>
    /// One hint per requirement with a Nexus id (off-site requirements cannot be checked and are skipped). A
    /// requirement pointing at the mod itself is ignored. <paramref name="requirements"/> overrides the entry's
    /// recorded list (for requirements fetched later and not yet stored); null uses the entry's list.
    /// </summary>
    public static IReadOnlyList<ModDependencyHint> Find(ModEntry mod, IEnumerable<ModInfo> installed, IReadOnlyList<ModRequirementRecord>? requirements = null)
    {
        ArgumentNullException.ThrowIfNull(mod);
        ArgumentNullException.ThrowIfNull(installed);

        var list = requirements ?? mod.Requirements;
        if (list is null || list.Count == 0)
        {
            return Array.Empty<ModDependencyHint>();
        }

        var nexusMods = installed.Where(m => m.Entry.Source == ModSource.Nexus && m.Entry.NexusModId is not null).ToList();
        var hints = new List<ModDependencyHint>();
        var seen = new HashSet<long>();
        foreach (var requirement in list)
        {
            if (requirement.NexusModId is not { } requiredId || requiredId == mod.NexusModId || !seen.Add(requiredId))
            {
                continue;
            }

            var match = nexusMods.FirstOrDefault(m => m.Entry.NexusModId == requiredId);
            var status = match is null
                ? DependencyStatus.NotInstalled
                : match.IsEnabled ? DependencyStatus.Satisfied : DependencyStatus.Disabled;
            hints.Add(new ModDependencyHint(requirement, status, match?.Entry));
        }

        return hints;
    }

    /// <summary>Only the requirements that are missing or disabled.</summary>
    public static IReadOnlyList<ModDependencyHint> FindProblems(ModEntry mod, IEnumerable<ModInfo> installed, IReadOnlyList<ModRequirementRecord>? requirements = null) =>
        Find(mod, installed, requirements).Where(h => !h.IsSatisfied).ToList();
}
