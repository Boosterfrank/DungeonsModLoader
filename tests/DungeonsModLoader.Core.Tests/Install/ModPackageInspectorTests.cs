using DungeonsModLoader.Core.Install;

namespace DungeonsModLoader.Core.Tests.Install;

public class ModPackageInspectorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dml-tests", "inspect-" + Guid.NewGuid().ToString("N"));

    public ModPackageInspectorTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Complete_set_at_the_root_is_one_default_candidate()
    {
        Touch("MyMod_P.pak", "MyMod_P.ucas", "MyMod_P.utoc");

        var contents = ModPackageInspector.Inspect(_root);

        var candidate = Assert.Single(contents.Candidates);
        Assert.Equal("Main", candidate.Name);
        Assert.Equal(string.Empty, candidate.RelativeDirectory);
        Assert.True(candidate.IsDefault);
        var set = Assert.Single(candidate.FileSets);
        Assert.True(set.IsComplete);
        Assert.Equal("MyMod", set.DisplayName);
        Assert.Equal("MyMod_P", set.DisplayPath);
        Assert.Empty(contents.ExtraFiles);
        Assert.Empty(contents.DocumentationFiles);
        Assert.Empty(contents.Warnings);
    }

    [Fact]
    public void Wrapper_folders_are_stripped()
    {
        Touch("Dungeons/Content/Paks/~mods/Wrapped/Wrapped_P.pak", "Dungeons/Content/Paks/~mods/Wrapped/Wrapped_P.ucas", "Dungeons/Content/Paks/~mods/Wrapped/Wrapped_P.utoc");

        var contents = ModPackageInspector.Inspect(_root);

        Assert.Equal(Path.Combine(_root, "Dungeons", "Content", "Paks", "~mods", "Wrapped"), contents.Root);
        var set = Assert.Single(Assert.Single(contents.Candidates).FileSets);
        Assert.Equal("Wrapped_P.pak", set.PakFile);
    }

    [Fact]
    public void Wrapper_stripping_stops_at_a_folder_with_files_or_several_sub_folders()
    {
        Touch("Pack/README.txt", "Pack/Mod/Mod_P.pak", "Pack/Mod/Mod_P.ucas", "Pack/Mod/Mod_P.utoc");

        var contents = ModPackageInspector.Inspect(_root);

        Assert.Equal(Path.Combine(_root, "Pack"), contents.Root);
        var candidate = Assert.Single(contents.Candidates);
        Assert.Equal("Mod", candidate.RelativeDirectory);
        Assert.Equal(["README.txt"], contents.DocumentationFiles);
    }

    [Fact]
    public void Variant_folders_become_separate_candidates_with_the_first_as_default()
    {
        Touch("Option B/B_P.pak", "Option B/B_P.ucas", "Option B/B_P.utoc", "Option A/A_P.pak", "Option A/A_P.ucas", "Option A/A_P.utoc");

        var contents = ModPackageInspector.Inspect(_root);

        Assert.Equal(2, contents.Candidates.Count);
        Assert.Equal("Option A", contents.Candidates[0].Name);
        Assert.True(contents.Candidates[0].IsDefault);
        Assert.Equal("Option B", contents.Candidates[1].Name);
        Assert.False(contents.Candidates[1].IsDefault);
        Assert.Equal("Option A/A_P", contents.Candidates[0].FileSets[0].DisplayPath);
    }

    [Fact]
    public void Root_sets_win_the_default_over_sub_folder_sets()
    {
        Touch("Main_P.pak", "Main_P.ucas", "Main_P.utoc", "Optional/Extra_P.pak", "Optional/Extra_P.ucas", "Optional/Extra_P.utoc");

        var contents = ModPackageInspector.Inspect(_root);

        Assert.Equal(2, contents.Candidates.Count);
        Assert.Equal("Main", contents.Candidates[0].Name);
        Assert.True(contents.Candidates[0].IsDefault);
        Assert.False(contents.Candidates[1].IsDefault);
    }

    [Fact]
    public void Incomplete_sets_are_reported_and_a_lone_pak_is_valid()
    {
        Touch("Broken_P.pak", "Broken_P.ucas", "Old_P.pak", "Orphan_P.utoc");

        var contents = ModPackageInspector.Inspect(_root);

        var sets = contents.Candidates.Single().FileSets.ToDictionary(s => s.BaseName);
        Assert.False(sets["Broken_P"].IsValid);
        Assert.Contains(".utoc", sets["Broken_P"].Warning);
        Assert.True(sets["Old_P"].IsPakOnly);
        Assert.True(sets["Old_P"].IsValid);
        Assert.False(sets["Orphan_P"].IsValid);
        Assert.Contains(".pak", sets["Orphan_P"].Warning);
        Assert.Equal(2, contents.Warnings.Count);
    }

    [Fact]
    public void Data_files_and_documentation_are_separated()
    {
        Touch("pak/Skins_P.pak", "pak/Skins_P.ucas", "pak/Skins_P.utoc", "skins/Steve.png", "capes/_defaults/Hero Cape.png", "READ_THIS_FILE.txt", "notes.md");

        var contents = ModPackageInspector.Inspect(_root);

        var candidate = Assert.Single(contents.Candidates);
        Assert.Equal("pak", candidate.RelativeDirectory);
        Assert.Equal(["capes/_defaults/Hero Cape.png", "skins/Steve.png"], contents.ExtraFiles.OrderBy(f => f, StringComparer.Ordinal).ToArray());
        Assert.Equal(["READ_THIS_FILE.txt", "notes.md"], contents.DocumentationFiles.OrderBy(f => f, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void Junk_is_ignored_and_grouping_is_case_insensitive()
    {
        Touch("__MACOSX/._x", "Thumbs.db", "mymod_P.pak", "MyMod_P.ucas", "MYMOD_P.utoc");

        var contents = ModPackageInspector.Inspect(_root);

        var set = Assert.Single(Assert.Single(contents.Candidates).FileSets);
        Assert.True(set.IsComplete);
        Assert.Empty(contents.ExtraFiles);
        Assert.Empty(contents.Warnings);
    }

    [Fact]
    public void Package_without_mod_files_has_no_candidates()
    {
        Touch("readme.txt", "picture.png");

        var contents = ModPackageInspector.Inspect(_root);

        Assert.Empty(contents.Candidates);
        Assert.Equal(["picture.png"], contents.ExtraFiles);
    }

    private void Touch(params string[] relativePaths)
    {
        foreach (var relative in relativePaths)
        {
            var full = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, "x");
        }
    }
}
