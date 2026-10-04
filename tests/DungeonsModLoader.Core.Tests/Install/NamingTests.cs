using DungeonsModLoader.Core.Install;

namespace DungeonsModLoader.Core.Tests.Install;

public class NamingTests
{
    [Theory]
    [InlineData("Second Skin Layer-10-1-2-0-1696000000", "Second Skin Layer", "1.2.0")]
    [InlineData("Cool Mod-5-1-2-1696000000", "Cool Mod", "1.2")]
    [InlineData("FreeCam v1.3", "FreeCam", "1.3")]
    [InlineData("Cool Mod v2", "Cool Mod", "2")]
    [InlineData("Skins 2", "Skins 2", null)]
    [InlineData("FreeCam_2.0.1", "FreeCam", "2.0.1")]
    [InlineData("BlueprintLoader", "BlueprintLoader", null)]
    [InlineData("My_Great_Mod", "My Great Mod", null)]
    [InlineData("  spaced   name  ", "spaced name", null)]
    [InlineData("", "Mod", null)]
    public void Archive_names_yield_name_and_version(string input, string expectedName, string? expectedVersion)
    {
        var (name, version) = ModNameHeuristics.FromArchiveName(input);

        Assert.Equal(expectedName, name);
        Assert.Equal(expectedVersion, version);
    }

    [Fact]
    public void File_set_name_drops_the_patch_suffix()
    {
        var set = new ModFileSet(string.Empty, "Second_Skin_P", "Second_Skin_P.pak", null, null);

        Assert.Equal("Second Skin", ModNameHeuristics.FromFileSet(set));
    }

    [Theory]
    [InlineData("Cool Mod", "Cool Mod")]
    [InlineData("What? A <mod>: v2|x", "What A mod v2 x")]
    [InlineData("Trailing dots...", "Trailing dots")]
    [InlineData("   ", "Mod")]
    [InlineData("CON", "Mod")]
    [InlineData("..", "Mod")]
    [InlineData("a/b\\c", "a b c")]
    public void Folder_names_are_sanitized(string input, string expected)
    {
        Assert.Equal(expected, FolderNameSanitizer.Sanitize(input));
    }

    [Fact]
    public void Long_folder_names_are_capped()
    {
        var name = FolderNameSanitizer.Sanitize(new string('a', 200));

        Assert.Equal(80, name.Length);
    }

    [Fact]
    public void MakeUnique_appends_a_counter()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Mod", "Mod (2)" };

        Assert.Equal("Mod (3)", FolderNameSanitizer.MakeUnique("Mod", taken.Contains));
        Assert.Equal("Other", FolderNameSanitizer.MakeUnique("Other", taken.Contains));
    }

    [Fact]
    public void FromPaths_groups_loose_files_and_separates_archives_and_folders()
    {
        var root = Path.Combine(Path.GetTempPath(), "dml-tests", "paths-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Folder"));
        var pak = Path.Combine(root, "A_P.pak");
        var utoc = Path.Combine(root, "A_P.utoc");
        var zip = Path.Combine(root, "b.zip");
        var junk = Path.Combine(root, "notes.docx");
        foreach (var file in new[] { pak, utoc, zip, junk })
        {
            File.WriteAllText(file, "x");
        }

        try
        {
            var sources = InstallSource.FromPaths([pak, zip, Path.Combine(root, "Folder"), utoc, junk], out var ignored);

            Assert.Equal(3, sources.Count);
            Assert.Equal(InstallSourceKind.Archive, sources[0].Kind);
            Assert.Equal(InstallSourceKind.Folder, sources[1].Kind);
            Assert.Equal(InstallSourceKind.LooseFiles, sources[2].Kind);
            Assert.Equal(2, sources[2].Paths.Count);
            Assert.Equal([junk], ignored);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
