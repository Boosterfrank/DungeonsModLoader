using DungeonsModLoader.Core.Mods;

namespace DungeonsModLoader.Core.Tests.Mods;

/// <summary>Conflict and dependency hints computed from manifest entries (no disk access).</summary>
public class ModHintsTests
{
    private static ModInfo Mod(string name, ModState state, params string[] files)
    {
        var entry = new ModEntry
        {
            FolderName = name,
            DisplayName = name,
            Files = files.Select(f => new ModFileRecord(f, "00", 1)).ToList(),
        };
        return new ModInfo { Entry = entry, State = state, FolderPath = state == ModState.Missing ? null : @"C:\mods\" + name };
    }

    private static ModInfo NexusMod(string name, long nexusId, ModState state, params ModRequirementRecord[] requirements)
    {
        var info = Mod(name, state, name + "_P.pak", name + "_P.ucas", name + "_P.utoc");
        info.Entry.Source = ModSource.Nexus;
        info.Entry.NexusModId = nexusId;
        info.Entry.Requirements = requirements.Length == 0 ? null : requirements.ToList();
        return info;
    }

    [Fact]
    public void Conflicts_are_reported_for_enabled_mods_sharing_a_pak_base_name()
    {
        var a = Mod("A", ModState.Enabled, "Hud_P.pak", "Hud_P.ucas", "Hud_P.utoc", "readme.txt");
        var b = Mod("B", ModState.Enabled, "sub/HUD_P.pak", "sub/HUD_P.ucas", "sub/HUD_P.utoc");
        var c = Mod("C", ModState.Enabled, "Other_P.pak");

        var conflicts = ModConflictDetector.Find(new[] { a, b, c });

        Assert.Equal(2, conflicts.Count);
        var forA = Assert.Single(conflicts[a.Entry.Id]);
        Assert.Same(b.Entry, forA.Other);
        Assert.Equal(new[] { "Hud_P" }, forA.SharedBaseNames, StringComparer.OrdinalIgnoreCase);
        Assert.Same(a.Entry, Assert.Single(conflicts[b.Entry.Id]).Other);
        Assert.False(conflicts.ContainsKey(c.Entry.Id));
    }

    [Fact]
    public void Disabled_and_missing_mods_never_conflict()
    {
        var a = Mod("A", ModState.Enabled, "Hud_P.pak");
        var b = Mod("B", ModState.Disabled, "Hud_P.pak");
        var c = Mod("C", ModState.Missing, "Hud_P.pak");

        var conflicts = ModConflictDetector.Find(new[] { a, b, c });

        Assert.Empty(conflicts);
    }

    [Fact]
    public void Shared_non_mod_files_do_not_count()
    {
        var a = Mod("A", ModState.Enabled, "readme.txt", "A_P.pak");
        var b = Mod("B", ModState.Enabled, "readme.txt", "B_P.pak");

        Assert.Empty(ModConflictDetector.Find(new[] { a, b }));
    }

    [Fact]
    public void Several_shared_sets_are_listed_once_per_other_mod()
    {
        var a = Mod("A", ModState.Enabled, "One_P.pak", "Two_P.pak");
        var b = Mod("B", ModState.Enabled, "One_P.pak", "Two_P.pak", "Three_P.pak");

        var conflicts = ModConflictDetector.Find(new[] { a, b });

        var conflict = Assert.Single(conflicts[a.Entry.Id]);
        Assert.Equal(new[] { "One_P", "Two_P" }, conflict.SharedBaseNames);
    }

    [Fact]
    public void Dependency_hints_report_missing_disabled_and_satisfied_requirements()
    {
        var loader = NexusMod("Loader", 2, ModState.Enabled);
        var skins = NexusMod("Skins", 5, ModState.Disabled);
        var mod = NexusMod(
            "Mod",
            100,
            ModState.Enabled,
            new ModRequirementRecord(2, "Blueprint Loader"),
            new ModRequirementRecord(5, "Skin Loader"),
            new ModRequirementRecord(77, "Something else"),
            new ModRequirementRecord(null, "Microsoft Visual C++", "https://example.test"),
            new ModRequirementRecord(100, "Itself"),
            new ModRequirementRecord(2, "Blueprint Loader (listed twice)"));
        var installed = new[] { loader, skins, mod };

        var hints = ModDependencyHints.Find(mod.Entry, installed);

        Assert.Equal(3, hints.Count);
        Assert.Equal(DependencyStatus.Satisfied, hints[0].Status);
        Assert.Same(loader.Entry, hints[0].InstalledMod);
        Assert.Equal(DependencyStatus.Disabled, hints[1].Status);
        Assert.Same(skins.Entry, hints[1].InstalledMod);
        Assert.Equal(DependencyStatus.NotInstalled, hints[2].Status);
        Assert.Null(hints[2].InstalledMod);

        var problems = ModDependencyHints.FindProblems(mod.Entry, installed);
        Assert.Equal(new[] { "Skin Loader", "Something else" }, problems.Select(p => p.Requirement.Name));
    }

    [Fact]
    public void Dependency_hints_are_empty_when_requirements_are_unknown_or_none()
    {
        var mod = NexusMod("Mod", 100, ModState.Enabled);
        Assert.Empty(ModDependencyHints.Find(mod.Entry, new[] { mod }));

        mod.Entry.Requirements = new List<ModRequirementRecord>();
        Assert.Empty(ModDependencyHints.Find(mod.Entry, new[] { mod }));
    }

    [Fact]
    public void Dependency_hints_use_the_override_list_when_given()
    {
        var mod = NexusMod("Mod", 100, ModState.Enabled);
        var fetched = new[] { new ModRequirementRecord(2, "Blueprint Loader") };

        var hints = ModDependencyHints.Find(mod.Entry, new[] { mod }, fetched);

        var hint = Assert.Single(hints);
        Assert.Equal(DependencyStatus.NotInstalled, hint.Status);
        Assert.Equal("Blueprint Loader", hint.Requirement.Name);
    }
}
