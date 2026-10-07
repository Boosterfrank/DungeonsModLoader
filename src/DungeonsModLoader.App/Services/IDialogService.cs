using DungeonsModLoader.Core.Install;

namespace DungeonsModLoader.App.Services;

/// <summary>One button of a <see cref="IDialogService.ChooseAsync"/> dialog.</summary>
/// <param name="Id">Returned when the button is chosen.</param>
/// <param name="Text">Button caption.</param>
/// <param name="IsPrimary">Rendered as the one gold action and triggered by Enter.</param>
/// <param name="IsDestructive">Rendered in the danger style.</param>
public sealed record DialogChoice(string Id, string Text, bool IsPrimary = false, bool IsDestructive = false);

/// <summary>Progress of a long operation shown by <see cref="IDialogService.RunWithProgressAsync{T}"/>. <paramref name="Fraction"/> is 0..1, or null for indeterminate.</summary>
public sealed record ProgressUpdate(string Message, double? Fraction = null);

/// <summary>What the user chose in the install picker: the file sets to install and the mod's display name.</summary>
public sealed record InstallPickerResult(IReadOnlyList<ModFileSet> Selected, string Name);

/// <summary>
/// Themed modal dialogs (the stock MessageBox is never used) and the system file / folder pickers. All methods must
/// be called on the UI thread and complete when the dialog closes.
/// </summary>
public interface IDialogService
{
    Task ShowInfoAsync(string title, string message);

    /// <summary>Friendly error with an optional expandable technical details block.</summary>
    Task ShowErrorAsync(string title, string message, string? details = null);

    /// <summary>A notice with one button and no other way out (no Esc, no close): used for a mandatory update.</summary>
    Task ShowRequiredAsync(string title, string message, string buttonText = "OK");

    /// <summary>Yes/No style question. <paramref name="isDestructive"/> renders the confirm button in the danger style.</summary>
    Task<bool> ConfirmAsync(string title, string message, string confirmText = "OK", string cancelText = "Cancel", bool isDestructive = false);

    /// <summary>
    /// Question with one button per choice (right-aligned, in the given order). Returns the chosen
    /// <see cref="DialogChoice.Id"/>, or null when the dialog was dismissed with Esc or closed.
    /// </summary>
    Task<string?> ChooseAsync(string title, string message, IReadOnlyList<DialogChoice> choices);

    /// <summary>Single-line text input; returns null when cancelled.</summary>
    Task<string?> PromptAsync(string title, string message, string? initialValue = null, string confirmText = "OK");

    /// <summary>Folder picker (system dialog); returns null when cancelled.</summary>
    string? PickFolder(string title, string? initialPath = null);

    /// <summary>
    /// File picker (system dialog). <paramref name="filter"/> uses the Win32 syntax
    /// ("Archives (*.zip)|*.zip|All files (*.*)|*.*"). Returns an empty list when cancelled.
    /// </summary>
    IReadOnlyList<string> PickFiles(string title, string filter, bool multiSelect);

    /// <summary>Save-as picker (system dialog). Returns the chosen path, or null when cancelled.</summary>
    string? PickSaveFile(string title, string filter, string? defaultFileName = null);

    /// <summary>
    /// Shows a progress dialog while <paramref name="work"/> runs. The work starts on the UI thread's async context
    /// (so it must do its file I/O asynchronously) and may report progress from any thread; the dialog closes when
    /// the task completes. The task's exception is re-thrown; cancelling the dialog cancels the token and the
    /// work's <see cref="OperationCanceledException"/> surfaces to the caller.
    /// </summary>
    Task<T> RunWithProgressAsync<T>(string title, Func<IProgress<ProgressUpdate>, CancellationToken, Task<T>> work, bool canCancel = true);

    /// <summary>
    /// Lets the user pick the file sets to install (and edit the mod name) when a package offers a choice.
    /// <paramref name="suggestedName"/> pre-fills the name (the Nexus mod name); null uses the archive-derived one.
    /// Returns null when cancelled.
    /// </summary>
    Task<InstallPickerResult?> ShowInstallPickerAsync(InstallPlan plan, string? suggestedName = null);
}
