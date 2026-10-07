using DungeonsModLoader.Core.AppUpdates;
using DungeonsModLoader.Nexus.Api;

namespace DungeonsModLoader.Core.Tests.Nexus;

public class ModImageListTests
{
    [Fact]
    public void Collects_the_header_picture_and_description_images_in_order_without_duplicates()
    {
        const string description = """
            [b]Screenshots[/b]
            [img]https://staticdelivery.nexusmods.com/mods/10391/images/100/shot1.png[/img]
            [img=https://i.imgur.com/abc.jpg]
            [img]https://staticdelivery.nexusmods.com/mods/10391/images/100/shot1.png[/img]
            [img]HTTPS://staticdelivery.nexusmods.com/mods/10391/images/100/header.png[/img]
            [img]ftp://example.test/no.png[/img]
            [img]not a url[/img]
            """;

        var images = ModImageList.Collect("https://staticdelivery.nexusmods.com/mods/10391/images/100/header.png", description);

        Assert.Equal(
            new[]
            {
                "https://staticdelivery.nexusmods.com/mods/10391/images/100/header.png",
                "https://staticdelivery.nexusmods.com/mods/10391/images/100/shot1.png",
                "https://i.imgur.com/abc.jpg",
            },
            images);
    }

    [Fact]
    public void Empty_inputs_give_an_empty_list()
    {
        Assert.Empty(ModImageList.Collect(null, null));
        Assert.Empty(ModImageList.Collect(" ", "no pictures here"));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    [InlineData(10, true)]
    public void Update_becomes_mandatory_after_two_outdated_starts(int outdatedStarts, bool mandatory)
    {
        Assert.Equal(mandatory, AppUpdatePolicy.IsMandatory(outdatedStarts));
    }
}
