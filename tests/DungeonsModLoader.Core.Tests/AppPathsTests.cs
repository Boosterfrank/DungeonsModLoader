using DungeonsModLoader.Core;

namespace DungeonsModLoader.Core.Tests;

public class AppPathsTests
{
    [Fact]
    public void Paths_are_rooted_under_the_given_root()
    {
        var root = Path.Combine(Path.GetTempPath(), "dml-tests-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root);

        Assert.Equal(Path.Combine(root, "settings.json"), paths.SettingsFile);
        Assert.Equal(Path.Combine(root, "manifest.json"), paths.ManifestFile);
        Assert.Equal(Path.Combine(root, "logs"), paths.LogsDirectory);
        Assert.StartsWith(root, paths.ProfilesDirectory);
    }

    [Fact]
    public void EnsureCreated_creates_all_directories()
    {
        var root = Path.Combine(Path.GetTempPath(), "dml-tests-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root);
        try
        {
            paths.EnsureCreated();

            Assert.True(Directory.Exists(paths.ProfilesDirectory));
            Assert.True(Directory.Exists(paths.CacheDirectory));
            Assert.True(Directory.Exists(paths.DownloadsDirectory));
            Assert.True(Directory.Exists(paths.LogsDirectory));
            Assert.True(Directory.Exists(paths.BackupsDirectory));
            Assert.True(Directory.Exists(paths.TempDirectory));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void Default_root_is_under_local_app_data()
    {
        var paths = new AppPaths();
        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppInfo.FolderName);
        Assert.Equal(expected, paths.Root);
    }
}
