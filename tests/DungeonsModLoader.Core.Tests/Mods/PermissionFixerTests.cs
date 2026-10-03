using System.Runtime.Versioning;
using DungeonsModLoader.Core.Game;
using DungeonsModLoader.Core.Permissions;

namespace DungeonsModLoader.Core.Tests.Mods;

[SupportedOSPlatform("windows")]
public class PermissionFixerTests
{
    private const string Sid = "S-1-5-21-1111111111-2222222222-3333333333-1001";

    [Fact]
    public void BuildCommand_creates_both_folders_and_grants_modify_by_sid()
    {
        var install = new GameInstallation { Root = @"C:\Games\Minecraft Dungeons II", Source = GameSource.Steam };
        var mods = @"C:\Games\Minecraft Dungeons II\Dungeons\Content\Paks\~mods";
        var disabled = @"C:\Games\Minecraft Dungeons II\Dungeons\DungeonsModLoader_Disabled";

        var command = PermissionFixer.BuildCommand(install, Sid);

        var expected =
            $"(if not exist \"{mods}\\\" mkdir \"{mods}\") & " +
            $"(if not exist \"{disabled}\\\" mkdir \"{disabled}\") & " +
            $"icacls \"{mods}\" /grant *{Sid}:(OI)(CI)M /T /Q && " +
            $"icacls \"{disabled}\" /grant *{Sid}:(OI)(CI)M /T /Q";
        Assert.Equal(expected, command);
    }

    [Fact]
    public void BuildCommand_quotes_paths_and_never_starts_with_a_quote()
    {
        var install = new GameInstallation { Root = @"D:\Xbox Games\Minecraft Dungeons II\Content", Source = GameSource.Xbox };

        var command = PermissionFixer.BuildCommand(install, Sid);

        // cmd.exe /c strips the outer quotes when the command line starts with one; ours starts with "(".
        Assert.StartsWith("(", command);
        Assert.Equal(2, command.Split("icacls ").Length - 1);
        Assert.Contains($"\"{install.ModsDirectory}\" /grant *{Sid}:(OI)(CI)M /T /Q", command);
        Assert.Contains($"\"{install.DisabledModsDirectory}\" /grant *{Sid}:(OI)(CI)M /T /Q", command);
        Assert.DoesNotContain("\"\"", command);
    }

    [Fact]
    public void BuildCommand_rejects_missing_sid()
    {
        var install = new GameInstallation { Root = @"C:\Games\X", Source = GameSource.Manual };

        Assert.Throws<ArgumentException>(() => PermissionFixer.BuildCommand(install, " "));
        Assert.Throws<ArgumentNullException>(() => PermissionFixer.BuildCommand(install, null!));
    }
}
