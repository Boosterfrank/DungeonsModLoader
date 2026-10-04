using System.IO.Compression;
using DungeonsModLoader.Core.Install;

namespace DungeonsModLoader.Core.Tests.Install;

public class ArchiveExtractorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dml-tests", "extract-" + Guid.NewGuid().ToString("N"));

    public ArchiveExtractorTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Zip_entries_are_extracted_with_their_folders_and_progress_is_reported()
    {
        var zip = Path.Combine(_root, "mod.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            Write(archive, "Mod/Mod_P.pak", "pak");
            Write(archive, "Mod/Mod_P.ucas", "ucas");
            Write(archive, "Mod/data/skin.png", "png");
            archive.CreateEntry("Mod/empty/");
        }

        var destination = Path.Combine(_root, "out");
        var reports = new List<InstallProgress>();
        var progress = new Progress<InstallProgress>(reports.Add);

        await ArchiveExtractor.ExtractAsync(zip, destination, progress);

        Assert.Equal("pak", await File.ReadAllTextAsync(Path.Combine(destination, "Mod", "Mod_P.pak")));
        Assert.Equal("png", await File.ReadAllTextAsync(Path.Combine(destination, "Mod", "data", "skin.png")));
        Assert.Equal(3, Directory.GetFiles(destination, "*", SearchOption.AllDirectories).Length);
        await Task.Delay(50); // Progress<T> posts asynchronously
        Assert.NotEmpty(reports);
    }

    [Fact]
    public async Task Entries_that_escape_the_destination_are_rejected()
    {
        var zip = Path.Combine(_root, "evil.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            Write(archive, "ok.pak", "ok");
            Write(archive, "../evil.txt", "evil");
        }

        var destination = Path.Combine(_root, "out");

        await Assert.ThrowsAsync<InstallPackageException>(() => ArchiveExtractor.ExtractAsync(zip, destination));

        Assert.False(File.Exists(Path.Combine(_root, "evil.txt")));
    }

    [Fact]
    public async Task Absolute_entry_paths_are_rejected()
    {
        var zip = Path.Combine(_root, "abs.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            Write(archive, "C:/Windows/evil.txt", "evil");
        }

        await Assert.ThrowsAsync<InstallPackageException>(() => ArchiveExtractor.ExtractAsync(zip, Path.Combine(_root, "out")));
    }

    [Fact]
    public async Task Damaged_archive_is_a_friendly_error()
    {
        var zip = Path.Combine(_root, "broken.zip");
        await File.WriteAllTextAsync(zip, "this is not an archive at all, just text");

        var ex = await Assert.ThrowsAsync<InstallPackageException>(() => ArchiveExtractor.ExtractAsync(zip, Path.Combine(_root, "out")));

        Assert.Contains("broken.zip", ex.Message);
    }

    [Fact]
    public async Task Extraction_can_be_cancelled()
    {
        var zip = Path.Combine(_root, "many.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            for (var i = 0; i < 50; i++)
            {
                Write(archive, $"file{i}.pak", new string('x', 1000));
            }
        }

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ArchiveExtractor.ExtractAsync(zip, Path.Combine(_root, "out"), null, cts.Token));
    }

    private static void Write(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var stream = new StreamWriter(entry.Open());
        stream.Write(content);
    }
}
