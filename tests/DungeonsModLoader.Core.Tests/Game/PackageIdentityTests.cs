using DungeonsModLoader.Core.Game.Xbox;

namespace DungeonsModLoader.Core.Tests.Game;

public class PackageIdentityTests
{
    [Theory]
    [InlineData("Microsoft.MinecraftDungeons2_1.1.1.0_x64__8wekyb3d8bbwe", "Microsoft.MinecraftDungeons2_8wekyb3d8bbwe")]
    [InlineData("Microsoft.MinecraftDungeons2_1.1.1.0_x64_8wekyb3d8bbwe", "Microsoft.MinecraftDungeons2_8wekyb3d8bbwe")]
    [InlineData("Microsoft.MinecraftDungeons2_1.1.1.0_x64_resource_8wekyb3d8bbwe", "Microsoft.MinecraftDungeons2_8wekyb3d8bbwe")]
    [InlineData("Microsoft.MinecraftDungeons2_8wekyb3d8bbwe", "Microsoft.MinecraftDungeons2_8wekyb3d8bbwe")]
    [InlineData("  Microsoft.MinecraftDungeons2_1.1.1.0_x64__8wekyb3d8bbwe  ", "Microsoft.MinecraftDungeons2_8wekyb3d8bbwe")]
    public void Family_name_is_name_plus_publisher_id(string fullName, string expected)
    {
        Assert.Equal(expected, PackageIdentity.GetFamilyName(fullName));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("NoUnderscores")]
    [InlineData("Only_Three_Parts")]
    [InlineData("_1.0_x64__pub")]
    [InlineData("Name_1.0_x64__")]
    public void Family_name_is_null_for_unusable_input(string? fullName)
    {
        Assert.Null(PackageIdentity.GetFamilyName(fullName));
    }

    [Fact]
    public void Name_is_the_part_before_the_first_underscore()
    {
        Assert.Equal("Microsoft.MinecraftDungeons2", PackageIdentity.GetName("Microsoft.MinecraftDungeons2_1.1.1.0_x64__8wekyb3d8bbwe"));
        Assert.Equal("Plain", PackageIdentity.GetName("Plain"));
        Assert.Null(PackageIdentity.GetName(null));
        Assert.Null(PackageIdentity.GetName(" "));
    }

    [Fact]
    public void AppUserModelId_combines_family_name_and_application_id()
    {
        Assert.Equal(
            "Microsoft.MinecraftDungeons2_8wekyb3d8bbwe!AppMinecraftDungeonsIIShipping",
            PackageIdentity.GetAppUserModelId("Microsoft.MinecraftDungeons2_1.1.1.0_x64__8wekyb3d8bbwe", "AppMinecraftDungeonsIIShipping"));

        Assert.Null(PackageIdentity.GetAppUserModelId("Microsoft.MinecraftDungeons2_1.1.1.0_x64__8wekyb3d8bbwe", null));
        Assert.Null(PackageIdentity.GetAppUserModelId("Microsoft.MinecraftDungeons2_1.1.1.0_x64__8wekyb3d8bbwe", ""));
        Assert.Null(PackageIdentity.GetAppUserModelId("bad", "App"));
    }
}
