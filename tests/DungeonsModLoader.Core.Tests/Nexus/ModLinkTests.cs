using DungeonsModLoader.Core.Mods;
using DungeonsModLoader.Nexus.Api;
using DungeonsModLoader.Nexus.Linking;

namespace DungeonsModLoader.Core.Tests.Nexus;

/// <summary>Name splitting, search texts and the scoring that picks a hand-installed mod's Nexus page.</summary>
public class ModLinkTests
{
    private static NexusMod Mod(long id, string name, string author = "someone", int downloads = 1000) =>
        new(id, name, "", null, author, author, "1.0", null, null, 10, downloads, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "published", true, false, null, false);

    [Theory]
    [InlineData("BlueprintLoader", "Blueprint|Loader")]
    [InlineData("Blueprint Loader", "Blueprint|Loader")]
    [InlineData("D1Stats", "D1|Stats")]
    [InlineData("DungeonsGUIX", "Dungeons|GUIX")]
    [InlineData("CurrencyHUD_P", "Currency|HUD")]
    [InlineData("custom-skin_loader v2", "custom|skin|loader|v2")]
    [InlineData("", "")]
    public void Splits_names_into_words(string text, string expected)
    {
        Assert.Equal(expected, string.Join('|', ModNameMatcher.Words(text)));
    }

    [Fact]
    public void Normalizes_to_letters_and_digits_and_drops_the_pak_suffix()
    {
        Assert.Equal("blueprintloader", ModNameMatcher.Normalize("Blueprint Loader"));
        Assert.Equal("blueprintloader", ModNameMatcher.Normalize("BlueprintLoader_P"));
        Assert.Equal("d1stats", ModNameMatcher.Normalize(" D1-Stats! "));
        Assert.Equal("", ModNameMatcher.Normalize(null));
    }

    [Fact]
    public void Search_texts_are_phrases_first_then_distinctive_words()
    {
        var queries = ModNameMatcher.SearchQueries(new[] { "BlueprintLoader", "BlueprintLoader", "BlueprintLoader_P" });
        Assert.Equal(new[] { "Blueprint Loader", "Blueprint", "Loader" }, queries);

        Assert.Equal(new[] { "Dungeons GUIX", "GUIX" }, ModNameMatcher.SearchQueries(new[] { "DungeonsGUIX" }));
        Assert.Empty(ModNameMatcher.SearchQueries(new[] { "", null, "_" }));
    }

    [Fact]
    public void The_mod_with_the_same_name_wins_over_a_mod_that_contains_it()
    {
        var names = new[] { "BlueprintLoader", "BlueprintLoader_P" };
        var exact = ModNameMatcher.Score(names, Mod(2, "Blueprint Loader", downloads: 29_000));
        var longer = ModNameMatcher.Score(names, Mod(95, "BetterBlueprintLoader", downloads: 1_000));
        var unrelated = ModNameMatcher.Score(names, Mod(11, "WASD Controls"));

        Assert.Equal(1.0, exact);
        Assert.InRange(longer, ModLinkCandidate.MediumThreshold, ModLinkCandidate.HighThreshold - 0.01);
        Assert.Equal(0, unrelated);
    }

    [Fact]
    public void Among_weak_fits_the_original_outranks_its_translation_and_partial_matches()
    {
        var names = new[] { "D1Stats", "D1Stats_P" };
        var original = new ModLinkCandidate(Mod(41, "All Item Stats - Real Damage - DPS - Armor and Comparison (D1 Stats)", downloads: 3052), 0);
        var translation = new ModLinkCandidate(Mod(78, "Simplified Chinese translation of D1 Stats Minecraft Dungeons-style item stats", downloads: 155), 0);
        var partial = new ModLinkCandidate(Mod(30, "Item Stats", downloads: 4571), 0);

        var scored = new[] { original, translation, partial }
            .Select(c => c with { Score = ModNameMatcher.Score(names, c.Mod) })
            .OrderByDescending(c => c.Score)
            .ToList();

        Assert.Equal(41, scored[0].Mod.ModId);
        Assert.Equal(78, scored[1].Mod.ModId);
        Assert.Equal(30, scored[2].Mod.ModId);
        Assert.True(scored[0].Score >= ModLinkCandidate.MediumThreshold, "the original should be preselected");
        Assert.Equal(ModLinkConfidence.Low, scored[2].Confidence);
    }

    [Fact]
    public void Suggestion_preselects_the_best_candidate_only_when_it_is_not_weak()
    {
        var entry = new ModEntry { FolderName = "X", DisplayName = "X" };
        var info = new ModInfo { Entry = entry, State = ModState.Enabled, FolderPath = @"C:\x" };
        var weak = new ModLinkCandidate(Mod(1, "Something"), 0.3);
        var good = new ModLinkCandidate(Mod(2, "X"), 0.95);

        Assert.Null(new ModLinkSuggestion(info, new[] { weak }).Best);
        Assert.Same(good, new ModLinkSuggestion(info, new[] { good, weak }).Best);
        Assert.Equal(ModLinkConfidence.High, good.Confidence);
        Assert.Equal(95, good.ScorePercent);
    }

    [Fact]
    public void Names_of_an_entry_include_display_name_folder_and_pak_base_names()
    {
        var entry = new ModEntry
        {
            FolderName = "CoolMod",
            DisplayName = "Cool Mod",
            Files =
            {
                new ModFileRecord("CoolMod_P.pak", "00", 1),
                new ModFileRecord("CoolMod_P.ucas", "00", 1),
                new ModFileRecord("sub/Extra_P.pak", "00", 1),
                new ModFileRecord("readme.txt", "00", 1),
            },
        };

        Assert.Equal(new[] { "Cool Mod", "CoolMod", "Extra" }, ModLinkFinder.NamesOf(entry));
    }
}
