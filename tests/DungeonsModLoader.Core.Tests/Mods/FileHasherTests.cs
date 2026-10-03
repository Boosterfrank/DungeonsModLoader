using System.Text;
using DungeonsModLoader.Core.Mods;

namespace DungeonsModLoader.Core.Tests.Mods;

public class FileHasherTests
{
    private const string Sha256OfAbc = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
    private const string Sha256OfEmpty = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    [Fact]
    public async Task Known_content_produces_known_lower_case_hash()
    {
        using var temp = new TempGameRoot();
        var file = Path.Combine(temp.DataDirectory, "abc.txt");
        await File.WriteAllTextAsync(file, "abc", new UTF8Encoding(false));

        var hash = await FileHasher.ComputeSha256Async(file);

        Assert.Equal(Sha256OfAbc, hash);
    }

    [Fact]
    public async Task Stream_overload_hashes_from_current_position()
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes("abc"));

        Assert.Equal(Sha256OfAbc, await FileHasher.ComputeSha256Async(stream));
    }

    [Fact]
    public async Task Hash_directory_records_every_file_with_forward_slash_paths()
    {
        using var temp = new TempGameRoot();
        var folder = temp.CreateMod("Skins");
        Directory.CreateDirectory(Path.Combine(folder, "skins", "deep"));
        await File.WriteAllTextAsync(Path.Combine(folder, "skins", "deep", "abc.bin"), "abc", new UTF8Encoding(false));
        await File.WriteAllTextAsync(Path.Combine(folder, "READ_THIS_FILE.txt"), string.Empty);

        var records = await FileHasher.HashDirectoryAsync(folder);

        Assert.Equal(5, records.Count);
        Assert.All(records, r => Assert.DoesNotContain('\\', r.RelativePath));
        Assert.Contains(records, r => r.RelativePath == "skins/deep/abc.bin" && r.Sha256 == Sha256OfAbc && r.Size == 3);
        Assert.Contains(records, r => r.RelativePath == "READ_THIS_FILE.txt" && r.Sha256 == Sha256OfEmpty && r.Size == 0);
        Assert.Contains(records, r => r.RelativePath == "Skins_P.pak");
        Assert.Contains(records, r => r.RelativePath == "Skins_P.ucas");
        Assert.Contains(records, r => r.RelativePath == "Skins_P.utoc");

        // Deterministic order (ordinal by relative path).
        Assert.Equal(records.OrderBy(r => r.RelativePath, StringComparer.Ordinal).Select(r => r.RelativePath), records.Select(r => r.RelativePath));
    }

    [Fact]
    public async Task Hash_directory_throws_for_missing_folder()
    {
        using var temp = new TempGameRoot();

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => FileHasher.HashDirectoryAsync(Path.Combine(temp.DataDirectory, "nope")));
    }

    [Fact]
    public void Relative_path_uses_forward_slashes()
    {
        var root = Path.Combine("C:", "root");
        var full = Path.Combine(root, "a", "b", "c.txt");

        Assert.Equal("a/b/c.txt", FileHasher.ToRelativePath(root, full));
    }
}
