using DungeonsModLoader.Nexus;

namespace DungeonsModLoader.Core.Tests.Nexus;

/// <summary>Website URLs the app opens for the user (all under the game's domain).</summary>
public class NexusUrlTests
{
    [Fact]
    public void Mod_page_and_files_tab()
    {
        Assert.Equal("https://www.nexusmods.com/minecraftdungeons2/mods/10", NexusConstants.ModPageUrl(10));
        Assert.Equal("https://www.nexusmods.com/minecraftdungeons2/mods/10?tab=files", NexusConstants.ModFilesUrl(10));
    }

    [Fact]
    public void File_download_page_opens_the_file_in_mod_manager_mode()
    {
        Assert.Equal(
            "https://www.nexusmods.com/minecraftdungeons2/mods/100?tab=files&file_id=235&nmm=1",
            NexusConstants.ModFileDownloadPageUrl(100, 235));
    }

    [Fact]
    public void Api_key_page_is_the_account_api_tab()
    {
        Assert.StartsWith("https://www.nexusmods.com/users/myaccount?tab=api", NexusConstants.ApiKeyPageUrl);
    }
}
