using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DungeonsModLoader.App.ViewModels.Setup;

/// <summary>
/// A mod folder that was already in <c>~mods</c> when setup ran and was added to the list automatically (setup
/// step 2). The contents summary ("3 files · 12.4 MB · 1 pak") is computed on a background thread so a large mod
/// never stalls the window.
/// </summary>
public sealed partial class ExistingModViewModel : ObservableObject
{
    public ExistingModViewModel(string displayName, string folderPath, bool isEnabled)
    {
        DisplayName = displayName;
        FolderPath = folderPath;
        IsEnabled = isEnabled;
    }

    public string DisplayName { get; }

    public string FolderPath { get; }

    /// <summary>False for a mod found in the disabled folder.</summary>
    public bool IsEnabled { get; }

    [ObservableProperty]
    private string _summary = "Scanning…";

    /// <summary>Fills <see cref="Summary"/> off the UI thread. Never throws.</summary>
    public async Task LoadSummaryAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            Summary = await Task.Run(() => Describe(FolderPath), cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // The window is closing; the summary no longer matters.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Summary = "Contents unavailable";
        }
    }

    /// <summary>"&lt;n&gt; files · &lt;size&gt;" plus " · &lt;k&gt; pak" when .pak files are present.</summary>
    public static string Describe(string folderPath)
    {
        var files = 0;
        var paks = 0;
        long bytes = 0;

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = 0,
        };

        foreach (var file in new DirectoryInfo(folderPath).EnumerateFiles("*", options))
        {
            files++;
            bytes += file.Length;
            if (string.Equals(file.Extension, ".pak", StringComparison.OrdinalIgnoreCase))
            {
                paks++;
            }
        }

        var text = string.Create(CultureInfo.CurrentCulture, $"{files} {(files == 1 ? "file" : "files")} · {FormatSize(bytes)}");
        return paks > 0 ? string.Create(CultureInfo.CurrentCulture, $"{text} · {paks} pak") : text;
    }

    /// <summary>Human-readable size: "512 B", "48 KB", "12.4 MB", "1.25 GB".</summary>
    public static string FormatSize(long bytes)
    {
        const double kilobyte = 1024;
        const double megabyte = kilobyte * 1024;
        const double gigabyte = megabyte * 1024;
        var culture = CultureInfo.CurrentCulture;

        if (bytes < kilobyte)
        {
            return string.Create(culture, $"{bytes} B");
        }

        if (bytes < megabyte)
        {
            return string.Create(culture, $"{bytes / kilobyte:0} KB");
        }

        if (bytes < gigabyte)
        {
            return string.Create(culture, $"{bytes / megabyte:0.#} MB");
        }

        return string.Create(culture, $"{bytes / gigabyte:0.##} GB");
    }
}
