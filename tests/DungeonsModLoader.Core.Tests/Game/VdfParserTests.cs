using DungeonsModLoader.Core.Game.Steam;

namespace DungeonsModLoader.Core.Tests.Game;

public class VdfParserTests
{
    private const string LibraryFoldersSample = """
        "libraryfolders"
        {
        	"0"
        	{
        		"path"		"C:\\Program Files (x86)\\Steam"
        		"label"		""
        		"contentid"		"324966128126941370"
        		"totalsize"		"0"
        		"apps"
        		{
        			"228980"		"171517476"
        			"1912410"		"10106430473"
        		}
        	}
        	"1"
        	{
        		"path"		"D:\\SteamLibrary"
        		"label"		"Games"
        		"apps"
        		{
        			"250820"		"5820607244"
        		}
        	}
        }
        """;

    private const string AppManifestSample = """
        "AppState"
        {
        	"appid"		"1912410"
        	"Universe"		"1"
        	"LauncherPath"		"C:\\Program Files (x86)\\Steam\\steam.exe"
        	"name"		"Minecraft Dungeons II"
        	"StateFlags"		"4"
        	"installdir"		"Minecraft Dungeons II"
        	"InstalledDepots"
        	{
        		"1912411"
        		{
        			"manifest"		"1012784868868940449"
        			"size"		"10106430473"
        		}
        	}
        }
        """;

    [Fact]
    public void Parses_libraryfolders_with_two_libraries_and_apps_blocks()
    {
        var root = VdfParser.Parse(LibraryFoldersSample);
        var libraries = root["libraryfolders"];

        Assert.NotNull(libraries);
        Assert.True(libraries.IsBlock);
        Assert.Equal(2, libraries.Children.Count);

        var first = libraries["0"]!;
        Assert.Equal(@"C:\Program Files (x86)\Steam", first.GetValue("path"));
        Assert.Equal(string.Empty, first.GetValue("label"));
        var apps = first["apps"]!;
        Assert.True(apps.IsBlock);
        Assert.Equal(2, apps.Children.Count);
        Assert.Equal("10106430473", apps.GetValue("1912410"));

        var second = libraries["1"]!;
        Assert.Equal(@"D:\SteamLibrary", second.GetValue("path"));
        Assert.Equal("Games", second.GetValue("label"));
        Assert.Single(second["apps"]!.Children);
    }

    [Fact]
    public void Parses_appmanifest_values_and_nested_depots()
    {
        var root = VdfParser.Parse(AppManifestSample);
        var state = root["AppState"];

        Assert.NotNull(state);
        Assert.Equal("1912410", state.GetValue("appid"));
        Assert.Equal("Minecraft Dungeons II", state.GetValue("installdir"));
        Assert.Equal("Minecraft Dungeons II", state.GetValue("name"));
        Assert.Equal(@"C:\Program Files (x86)\Steam\steam.exe", state.GetValue("LauncherPath"));

        var depot = state["InstalledDepots"]!["1912411"]!;
        Assert.Equal("1012784868868940449", depot.GetValue("manifest"));
        Assert.Null(state.GetValue("InstalledDepots")); // a block has no value
    }

    [Fact]
    public void Lookups_are_case_insensitive()
    {
        var root = VdfParser.Parse(AppManifestSample);

        Assert.NotNull(root["appstate"]);
        Assert.Equal("1912410", root["APPSTATE"]!.GetValue("AppId"));
        Assert.Null(root["missing"]);
        Assert.Null(root["AppState"]!.GetValue("missing"));
    }

    [Fact]
    public void Unescapes_quotes_and_backslashes_inside_values()
    {
        var text = """
            "root"
            {
                "quoted"   "say \"hi\" to \\everyone\\"
                "tabbed"   "a\tb"
                "unknown"  "keep \q verbatim"
            }
            """;

        var node = VdfParser.Parse(text)["root"]!;

        Assert.Equal("say \"hi\" to \\everyone\\", node.GetValue("quoted"));
        Assert.Equal("a\tb", node.GetValue("tabbed"));
        Assert.Equal("keep \\q verbatim", node.GetValue("unknown"));
    }

    [Fact]
    public void Ignores_line_comments_and_bom_and_crlf()
    {
        var text = "\uFEFF// leading comment\r\n\"root\" // trailing\r\n{\r\n\t\"key\"\t\"value\" // after value\r\n\t// \"commented\" \"out\"\r\n\t\"url\"\t\"http://example.com/x\"\r\n}\r\n";

        var node = VdfParser.Parse(text)["root"]!;

        Assert.Equal(2, node.Children.Count);
        Assert.Equal("value", node.GetValue("key"));
        Assert.Equal("http://example.com/x", node.GetValue("url"));
        Assert.Null(node.GetValue("commented"));
    }

    [Fact]
    public void Parses_old_flat_libraryfolders_format_with_bare_tokens()
    {
        var text = """
            "LibraryFolders"
            {
                "TimeNextStatsReport"   "1600000000"
                "ContentStatsID"        "-1234"
                "1"     "D:\\SteamLibrary"
                2       E:\Games\Steam
            }
            """;

        var node = VdfParser.Parse(text)["libraryfolders"]!;

        Assert.Equal(@"D:\SteamLibrary", node.GetValue("1"));
        Assert.Equal(@"E:\Games\Steam", node.GetValue("2"));
        Assert.False(node["1"]!.IsBlock);
    }

    [Fact]
    public void Preserves_duplicate_keys_and_returns_first_on_lookup()
    {
        var node = VdfParser.Parse("\"r\" { \"k\" \"1\" \"k\" \"2\" }")["r"]!;

        Assert.Equal("1", node.GetValue("k"));
        Assert.Equal(["1", "2"], node.FindAll("k").Select(n => n.Value!).ToArray());
    }

    [Fact]
    public void Tolerates_malformed_input_without_throwing()
    {
        Assert.Empty(VdfParser.Parse(string.Empty).Children);
        Assert.Empty(VdfParser.Parse("   \r\n  ").Children);

        var unbalanced = VdfParser.Parse("\"r\" { \"k\" \"v\" ");
        Assert.Equal("v", unbalanced["r"]!.GetValue("k"));

        var dangling = VdfParser.Parse("\"r\" { \"k\" \"v\" \"orphan\" }");
        Assert.Single(dangling["r"]!.Children);

        var strayClose = VdfParser.Parse("} \"r\" { \"k\" \"v\" } }");
        Assert.Equal("v", strayClose["r"]!.GetValue("k"));

        var unterminatedQuote = VdfParser.Parse("\"r\" { \"k\" \"never closed");
        Assert.Equal("never closed", unterminatedQuote["r"]!.GetValue("k"));
    }

    [Fact]
    public void Skips_include_directives_and_conditionals()
    {
        var text = """
            #base "shared.vdf"
            "r"
            {
                "k" "v" [$WIN32]
                "other" "w"
            }
            """;

        var node = VdfParser.Parse(text)["r"]!;

        Assert.Equal(2, node.Children.Count);
        Assert.Equal("v", node.GetValue("k"));
        Assert.Equal("w", node.GetValue("other"));
    }

    [Fact]
    public void ParseFile_reads_a_file_with_bom()
    {
        using var temp = new TempDirectory();
        var path = temp.Sub("sample.acf");
        File.WriteAllText(path, AppManifestSample, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var root = VdfParser.ParseFile(path);

        Assert.Equal("1912410", root["AppState"]!.GetValue("appid"));
    }
}
