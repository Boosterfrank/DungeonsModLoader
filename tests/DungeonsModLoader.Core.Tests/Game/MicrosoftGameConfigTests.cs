using System.Xml;
using DungeonsModLoader.Core.Game.Xbox;

namespace DungeonsModLoader.Core.Tests.Game;

public class MicrosoftGameConfigTests
{
    /// <summary>Shape of the real file shipped with the game (trimmed resource list).</summary>
    private const string RealShape = """
        <?xml version="1.0" encoding="utf-8"?>
        <Game configVersion="1">
          <Identity Name="Microsoft.MinecraftDungeons2" Publisher="CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US" Version="1.1.1.0" />
          <ShellVisuals DefaultDisplayName="Minecraft Dungeons II" PublisherDisplayName="Microsoft Studios" StoreLogo="Resources\StoreLogo.png" Square150x150Logo="Resources\Logo.png" Square44x44Logo="Resources\SmallLogo.png" Square480x480Logo="Resources\Square480x480Logo.png" Description="ms-resource:AppDescription" ForegroundText="dark" BackgroundColor="#000040" SplashScreenImage="Resources\SplashScreen.png" />
          <Resources>
            <Resource Language="en" />
            <Resource Language="sv-SE" />
          </Resources>
          <ExecutableList>
            <Executable Name="Dungeons.exe" Id="AppMinecraftDungeonsIIShipping" OverrideDisplayName="ms-resource:AppDisplayName" TargetDeviceFamily="PC" />
          </ExecutableList>
          <TitleId>6B9DE498</TitleId>
          <MSAAppId>00000000497C1B94</MSAAppId>
          <StoreId>9P5786PJB9RP</StoreId>
          <RequiresXboxLive>false</RequiresXboxLive>
          <DesktopRegistration>
            <ProcessorArchitecture>x64</ProcessorArchitecture>
            <MultiplayerProtocol>true</MultiplayerProtocol>
            <DependencyList>
              <KnownDependency Name="VC14" />
            </DependencyList>
          </DesktopRegistration>
        </Game>
        """;

    [Fact]
    public void Parses_the_real_file_shape()
    {
        var config = MicrosoftGameConfig.Parse(RealShape);

        Assert.Equal("Microsoft.MinecraftDungeons2", config.IdentityName);
        Assert.StartsWith("CN=Microsoft Corporation", config.IdentityPublisher);
        Assert.Equal("1.1.1.0", config.IdentityVersion);
        Assert.Equal("Minecraft Dungeons II", config.DefaultDisplayName);
        Assert.Equal(["AppMinecraftDungeonsIIShipping"], config.ExecutableIds);
        Assert.Equal(["Dungeons.exe"], config.ExecutableNames);
        Assert.Equal("AppMinecraftDungeonsIIShipping", config.MainExecutableId);
    }

    [Fact]
    public void Parse_tolerates_a_leading_bom()
    {
        var config = MicrosoftGameConfig.Parse("﻿" + RealShape);

        Assert.Equal("Minecraft Dungeons II", config.DefaultDisplayName);
    }

    [Fact]
    public void First_executable_is_the_main_one_and_order_is_kept()
    {
        var config = MicrosoftGameConfig.Parse("""
            <Game>
              <ExecutableList>
                <Executable Name="Launcher.exe" Id="Launcher" />
                <Executable Name="Game.exe" Id="Game" />
                <Executable Name="NoId.exe" />
              </ExecutableList>
            </Game>
            """);

        Assert.Equal(["Launcher", "Game"], config.ExecutableIds);
        Assert.Equal(["Launcher.exe", "Game.exe", "NoId.exe"], config.ExecutableNames);
        Assert.Equal("Launcher", config.MainExecutableId);
    }

    [Fact]
    public void Missing_nodes_yield_nulls_and_empty_lists()
    {
        var config = MicrosoftGameConfig.Parse("<Game configVersion=\"1\" />");

        Assert.Null(config.IdentityName);
        Assert.Null(config.IdentityPublisher);
        Assert.Null(config.IdentityVersion);
        Assert.Null(config.DefaultDisplayName);
        Assert.Empty(config.ExecutableIds);
        Assert.Empty(config.ExecutableNames);
        Assert.Null(config.MainExecutableId);
    }

    [Fact]
    public void Malformed_xml_throws_from_parse_but_tryload_returns_null()
    {
        using var temp = new TempDirectory();
        var path = temp.Sub("MicrosoftGame.config");
        File.WriteAllText(path, "<Game><Identity Name='x'></Game>");

        Assert.Throws<XmlException>(() => MicrosoftGameConfig.Parse("<Game><Identity Name='x'></Game>"));
        Assert.Null(MicrosoftGameConfig.TryLoad(path));
        Assert.Null(MicrosoftGameConfig.TryLoad(temp.Sub("missing.config")));
    }

    [Fact]
    public void Load_reads_a_file_with_bom()
    {
        using var temp = new TempDirectory();
        FakeGame.WriteMicrosoftGameConfig(temp.Path);

        var config = MicrosoftGameConfig.TryLoad(temp.Sub("MicrosoftGame.config"));

        Assert.NotNull(config);
        Assert.Equal("Microsoft.MinecraftDungeons2", config.IdentityName);
        Assert.Equal("AppMinecraftDungeonsIIShipping", config.MainExecutableId);
    }
}
