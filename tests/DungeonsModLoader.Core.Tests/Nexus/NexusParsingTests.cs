using DungeonsModLoader.Nexus;
using DungeonsModLoader.Nexus.Api;
using DungeonsModLoader.Nexus.Nxm;
using DungeonsModLoader.Nexus.Text;
using DungeonsModLoader.Nexus.Updates;

namespace DungeonsModLoader.Core.Tests.Nexus;

public class NxmLinkTests
{
    [Fact]
    public void Parses_a_free_user_link_with_token()
    {
        var ok = NxmLink.TryParse("nxm://minecraftdungeons2/mods/100/files/235?key=AbC-123&expires=1800000000&user_id=42", out var link);

        Assert.True(ok);
        Assert.Equal("minecraftdungeons2", link!.GameDomain);
        Assert.Equal(100, link.ModId);
        Assert.Equal(235, link.FileId);
        Assert.Equal("AbC-123", link.Key);
        Assert.Equal(1800000000, link.Expires);
        Assert.Equal(42, link.UserId);
        Assert.True(link.HasToken);
        Assert.True(link.IsForThisGame);
        Assert.DoesNotContain("AbC-123", link.ToString());
    }

    [Fact]
    public void Parses_a_premium_link_without_token_and_other_game_casing()
    {
        Assert.True(NxmLink.TryParse("NXM://MinecraftDungeons2/mods/7/files/8", out var link));
        Assert.False(link!.HasToken);
        Assert.True(link.IsForThisGame);

        Assert.True(NxmLink.TryParse("nxm://skyrimse/mods/7/files/8", out var other));
        Assert.False(other!.IsForThisGame);
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://www.nexusmods.com/minecraftdungeons2/mods/100")]
    [InlineData("nxm://minecraftdungeons2/collections/abc/revisions/1")]
    [InlineData("nxm://minecraftdungeons2/mods/x/files/1")]
    [InlineData("nxm://minecraftdungeons2/mods/1/files/0")]
    public void Rejects_non_mod_links(string text)
    {
        Assert.False(NxmLink.TryParse(text, out var link));
        Assert.Null(link);
    }

    [Fact]
    public void Expired_tokens_are_detected()
    {
        Assert.True(NxmLink.TryParse("nxm://minecraftdungeons2/mods/1/files/2?key=k&expires=1000", out var link));
        Assert.True(link!.IsExpired);
    }
}

public class SecretMaskerTests
{
    [Fact]
    public void Masks_download_tokens_and_api_keys()
    {
        Assert.Equal("nxm://g/mods/1/files/2?key=***&expires=5", SecretMasker.Mask("nxm://g/mods/1/files/2?key=SeCrEt-123&expires=5"));
        Assert.Equal("apikey: ***", SecretMasker.Mask("apikey: AbCdEfGhIjKlMnOp"));
        Assert.Equal(new[] { "--data-dir", "C:\\x", "nxm://g/mods/1/files/2?key=***" }, SecretMasker.Mask(new[] { "--data-dir", "C:\\x", "nxm://g/mods/1/files/2?key=abc" }));
        Assert.Equal("…7890 (12 chars)", SecretMasker.Describe("123456567890"));
    }
}

public class NexusRateLimitTests
{
    [Theory]
    [InlineData("2019-02-01T12:00:00+00:00", 2019, 2, 1, 12)]
    [InlineData("2019-02-02 00:00:00 +0000", 2019, 2, 2, 0)]
    [InlineData("2019-02-02 00:00:00 +00:00", 2019, 2, 2, 0)]
    public void Parses_both_documented_reset_formats(string text, int year, int month, int day, int hour)
    {
        var parsed = NexusRateLimit.ParseDate(text);

        Assert.NotNull(parsed);
        Assert.Equal(new DateTimeOffset(year, month, day, hour, 0, 0, TimeSpan.Zero), parsed!.Value.ToUniversalTime());
    }

    [Fact]
    public void Resume_time_follows_the_exhausted_budget()
    {
        var hourly = DateTimeOffset.UtcNow.AddMinutes(20);
        var daily = DateTimeOffset.UtcNow.AddHours(5);
        Assert.Equal(hourly, new NexusRateLimit(100, 0, hourly, 2500, 10, daily, DateTimeOffset.UtcNow).ResumeAt);
        Assert.Equal(daily, new NexusRateLimit(100, 5, hourly, 2500, 0, daily, DateTimeOffset.UtcNow).ResumeAt);
        Assert.Null(new NexusRateLimit(100, 5, hourly, 2500, 10, daily, DateTimeOffset.UtcNow).ResumeAt);
    }
}

public class BbCodeTests
{
    [Fact]
    public void Renders_formatting_links_lists_and_breaks()
    {
        const string input = "\n<br />[size=5][b]Title[/b][/size]\n<br />\n<br />Hello [i]there[/i], see [url=https://example.com/x]the page[/url].\n<br />[list=1]\n<br />[*]First[/*]\n<br />[*]Second &#92; item[/*]\n<br />[/list]Done [img]https://img/x.png[/img]";

        var nodes = BbCode.Parse(input);
        var text = BbCode.ToPlainText(input);

        var title = Assert.IsType<BbText>(nodes[0]);
        Assert.Equal("Title", title.Text);
        Assert.True(title.Bold);
        Assert.Equal(2, title.HeadingLevel);
        var link = nodes.OfType<BbText>().Single(n => n.Url is not null);
        Assert.Equal("the page", link.Text);
        Assert.Equal("https://example.com/x", link.Url);
        Assert.Equal(2, nodes.OfType<BbListItem>().Count());
        Assert.All(nodes.OfType<BbListItem>(), item => Assert.True(item.Ordered));
        Assert.Single(nodes.OfType<BbImage>());
        Assert.Contains("1. First", text);
        Assert.Contains("2. Second \\ item", text);
        Assert.Contains("the page (https://example.com/x)", text);
        Assert.DoesNotContain("[b]", text);
        Assert.DoesNotContain("<br", text);
    }

    [Fact]
    public void Unknown_tags_are_dropped_but_their_text_kept()
    {
        Assert.Equal("Red text centered", BbCode.ToPlainText("[color=red]Red text[/color] [center]centered[/center]"));
        Assert.Equal("", BbCode.ToPlainText(null));
        Assert.Equal("plain", BbCode.ToPlainText("plain"));
    }

    [Fact]
    public void Collapses_long_runs_of_blank_lines()
    {
        var text = BbCode.ToPlainText("a\n\n\n\n\nb");
        Assert.Equal("a\n\nb", text);
    }
}

public class UpdateResolverTests
{
    private static NexusFile File(long id, NexusFileCategory category, int day, string version = "1") =>
        new(id, "f" + id, version, category, category == NexusFileCategory.Main, 100, "f" + id + ".zip", new DateTimeOffset(2026, 1, day, 0, 0, 0, TimeSpan.Zero), null, null, null);

    [Fact]
    public void Follows_the_update_chain_to_its_end()
    {
        var files = new NexusFileList(
            new[] { File(1, NexusFileCategory.OldVersion, 1), File(2, NexusFileCategory.OldVersion, 2), File(3, NexusFileCategory.Main, 3) },
            new[] { new NexusFileUpdate(1, 2, null, null, DateTimeOffset.UtcNow), new NexusFileUpdate(2, 3, null, null, DateTimeOffset.UtcNow) });

        Assert.Equal(3, UpdateResolver.FindNewerFile(1, files)!.FileId);
        Assert.Equal(3, UpdateResolver.FindNewerFile(2, files)!.FileId);
        Assert.Null(UpdateResolver.FindNewerFile(3, files));
    }

    [Fact]
    public void Falls_back_to_the_newest_main_file_for_main_installs_only()
    {
        var files = new NexusFileList(
            new[] { File(1, NexusFileCategory.Main, 1), File(2, NexusFileCategory.Main, 5), File(9, NexusFileCategory.Optional, 2) },
            Array.Empty<NexusFileUpdate>());

        Assert.Equal(2, UpdateResolver.FindNewerFile(1, files)!.FileId);
        Assert.Null(UpdateResolver.FindNewerFile(9, files));
        Assert.Null(UpdateResolver.FindNewerFile(2, files));
        // Linked without a known version: the newest main file is the one to have.
        Assert.Equal(2, UpdateResolver.FindNewerFile(null, files)!.FileId);
    }

    [Fact]
    public void A_newer_main_file_counts_as_an_update_but_optional_and_older_files_do_not()
    {
        var files = new NexusFileList(
            new[] { File(1, NexusFileCategory.OldVersion, 1), File(2, NexusFileCategory.Main, 5), File(3, NexusFileCategory.Main, 7), File(9, NexusFileCategory.Optional, 8) },
            new[] { new NexusFileUpdate(1, 2, null, null, DateTimeOffset.UtcNow) });

        Assert.True(UpdateResolver.IsNewerVersion(1, files.Files[1], files));   // the successor the author marked
        Assert.True(UpdateResolver.IsNewerVersion(1, files.Files[2], files));   // a later main file
        Assert.True(UpdateResolver.IsNewerVersion(2, files.Files[2], files));
        Assert.False(UpdateResolver.IsNewerVersion(3, files.Files[1], files));  // older main file: a downgrade, ask
        Assert.False(UpdateResolver.IsNewerVersion(2, files.Files[3], files));  // optional file: another flavour, ask
        Assert.False(UpdateResolver.IsNewerVersion(2, files.Files[1], files));  // same file: a reinstall, ask
        Assert.True(UpdateResolver.IsNewerVersion(null, files.Files[1], files)); // unknown version: any main file
        Assert.False(UpdateResolver.IsNewerVersion(null, files.Files[3], files));
        Assert.True(UpdateResolver.IsNewerVersion(42, files.Files[2], files));  // installed file no longer listed
    }

    [Fact]
    public void A_file_no_longer_listed_is_replaced_by_the_current_main_file()
    {
        var files = new NexusFileList(new[] { File(5, NexusFileCategory.Main, 3) }, Array.Empty<NexusFileUpdate>());

        Assert.Equal(5, UpdateResolver.FindNewerFile(1, files)!.FileId);
    }

    [Fact]
    public void Removed_chain_targets_are_ignored()
    {
        var files = new NexusFileList(
            new[] { File(1, NexusFileCategory.Main, 1), File(2, NexusFileCategory.Removed, 2) },
            new[] { new NexusFileUpdate(1, 2, null, null, DateTimeOffset.UtcNow) });

        Assert.Null(UpdateResolver.FindNewerFile(1, files));
    }
}
