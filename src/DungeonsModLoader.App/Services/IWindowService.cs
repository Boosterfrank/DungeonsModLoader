namespace DungeonsModLoader.App.Services;

/// <summary>
/// Window-level operations that view models need but must not perform themselves
/// (opening secondary windows, launching URLs / folders in the shell).
/// </summary>
public interface IWindowService
{
    /// <summary>Opens the theme swatch / control gallery window (design review tool).</summary>
    void ShowSwatchWindow();

    /// <summary>Opens a web URL in the user's default browser.</summary>
    void OpenUrl(string url);

    /// <summary>Opens a folder in File Explorer (creating nothing; no-op if it does not exist).</summary>
    void OpenFolder(string path);
}
