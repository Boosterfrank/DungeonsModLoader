namespace DungeonsModLoader.App.Services;

/// <summary>
/// Themed modal dialogs (the stock MessageBox is never used) and the folder picker. All methods must be called
/// on the UI thread and complete when the dialog closes.
/// </summary>
public interface IDialogService
{
    Task ShowInfoAsync(string title, string message);

    /// <summary>Friendly error with an optional expandable technical details block.</summary>
    Task ShowErrorAsync(string title, string message, string? details = null);

    /// <summary>Yes/No style question. <paramref name="isDestructive"/> renders the confirm button in the danger style.</summary>
    Task<bool> ConfirmAsync(string title, string message, string confirmText = "OK", string cancelText = "Cancel", bool isDestructive = false);

    /// <summary>Single-line text input; returns null when cancelled.</summary>
    Task<string?> PromptAsync(string title, string message, string? initialValue = null, string confirmText = "OK");

    /// <summary>Folder picker (system dialog); returns null when cancelled.</summary>
    string? PickFolder(string title, string? initialPath = null);
}
