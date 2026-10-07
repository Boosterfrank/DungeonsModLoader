using System.IO;
using System.Windows;
using System.Windows.Threading;
using DungeonsModLoader.App.Views.Dialogs;
using DungeonsModLoader.Core;
using DungeonsModLoader.Core.Install;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace DungeonsModLoader.App.Services;

/// <summary>
/// Default <see cref="IDialogService"/>: themed <see cref="MessageDialog"/> / <see cref="PromptDialog"/> /
/// <see cref="ProgressDialog"/> / <see cref="InstallPickerDialog"/> windows shown modally over the active window
/// (or centred on screen when none is up yet), plus the system file and folder pickers. Calls from a non-UI
/// thread are marshalled to the dispatcher; each method completes when its dialog closes.
/// </summary>
public sealed class DialogService : IDialogService
{
    private readonly AppPaths _paths;
    private readonly IWindowService _windows;
    private readonly ILogger<DialogService> _logger;

    public DialogService(AppPaths paths, IWindowService windows, ILogger<DialogService> logger)
    {
        _paths = paths;
        _windows = windows;
        _logger = logger;
    }

    public Task ShowInfoAsync(string title, string message) =>
        ShowMessageAsync(new MessageDialogOptions
        {
            Kind = MessageDialogKind.Info,
            Title = title,
            Message = message,
        });

    public Task ShowErrorAsync(string title, string message, string? details = null) =>
        ShowMessageAsync(new MessageDialogOptions
        {
            Kind = MessageDialogKind.Error,
            Title = title,
            Message = message,
            Details = details,
            OpenLogsFolder = () => _windows.OpenFolder(_paths.LogsDirectory),
        });

    public Task ShowRequiredAsync(string title, string message, string buttonText = "OK") =>
        ShowMessageAsync(new MessageDialogOptions
        {
            Kind = MessageDialogKind.Info,
            Title = title,
            Message = message,
            ConfirmText = buttonText,
            AllowDismiss = false,
        });

    public Task<bool> ConfirmAsync(string title, string message, string confirmText = "OK", string cancelText = "Cancel", bool isDestructive = false) =>
        ShowMessageAsync(new MessageDialogOptions
        {
            Kind = MessageDialogKind.Confirm,
            Title = title,
            Message = message,
            ConfirmText = confirmText,
            CancelText = cancelText,
            IsDestructive = isDestructive,
        });

    public Task<string?> ChooseAsync(string title, string message, IReadOnlyList<DialogChoice> choices)
    {
        ArgumentNullException.ThrowIfNull(choices);
        if (choices.Count == 0)
        {
            throw new ArgumentException("A choice dialog needs at least one choice.", nameof(choices));
        }

        return OnUiThreadAsync(() =>
        {
            var dialog = new MessageDialog();
            dialog.Present(new MessageDialogOptions
            {
                Kind = MessageDialogKind.Choice,
                Title = title,
                Message = message,
                Choices = choices,
            });
            ShowModal(dialog);
            _logger.LogDebug("Choice dialog '{Title}' closed ({Outcome})", title, dialog.ChosenId ?? "dismissed");
            return dialog.ChosenId;
        });
    }

    public Task<string?> PromptAsync(string title, string message, string? initialValue = null, string confirmText = "OK") =>
        OnUiThreadAsync(() =>
        {
            var dialog = new PromptDialog();
            dialog.Present(title, message, initialValue, confirmText);
            ShowModal(dialog);
            _logger.LogDebug("Prompt '{Title}' closed ({Outcome})", title, dialog.Result is null ? "cancelled" : "confirmed");
            return dialog.Result;
        });

    public string? PickFolder(string title, string? initialPath = null)
    {
        var dialog = new OpenFolderDialog
        {
            Title = title,
            Multiselect = false,
        };

        if (!string.IsNullOrWhiteSpace(initialPath))
        {
            try
            {
                var fullPath = Path.GetFullPath(initialPath);
                if (Directory.Exists(fullPath))
                {
                    dialog.InitialDirectory = fullPath;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // An unusable initial path just means the picker opens at its default location.
            }
        }

        var owner = FindOwner(null);
        var picked = owner is not null ? dialog.ShowDialog(owner) : dialog.ShowDialog();
        if (picked != true || string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            _logger.LogDebug("Folder picker '{Title}' cancelled", title);
            return null;
        }

        _logger.LogDebug("Folder picker '{Title}' returned {Folder}", title, dialog.FolderName);
        return dialog.FolderName;
    }

    public IReadOnlyList<string> PickFiles(string title, string filter, bool multiSelect)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = filter,
            Multiselect = multiSelect,
            CheckFileExists = true,
            CheckPathExists = true,
            DereferenceLinks = true,
        };

        var owner = FindOwner(null);
        var picked = owner is not null ? dialog.ShowDialog(owner) : dialog.ShowDialog();
        if (picked != true || dialog.FileNames.Length == 0)
        {
            _logger.LogDebug("File picker '{Title}' cancelled", title);
            return Array.Empty<string>();
        }

        var files = dialog.FileNames.Where(f => !string.IsNullOrWhiteSpace(f)).ToList();
        _logger.LogDebug("File picker '{Title}' returned {Count} file(s)", title, files.Count);
        return files;
    }

    public string? PickSaveFile(string title, string filter, string? defaultFileName = null)
    {
        var dialog = new SaveFileDialog
        {
            Title = title,
            Filter = filter,
            FileName = defaultFileName ?? string.Empty,
            AddExtension = true,
            OverwritePrompt = true,
            CheckPathExists = true,
        };

        var owner = FindOwner(null);
        var picked = owner is not null ? dialog.ShowDialog(owner) : dialog.ShowDialog();
        if (picked != true || string.IsNullOrWhiteSpace(dialog.FileName))
        {
            _logger.LogDebug("Save picker '{Title}' cancelled", title);
            return null;
        }

        _logger.LogDebug("Save picker '{Title}' returned {File}", title, dialog.FileName);
        return dialog.FileName;
    }

    public Task<T> RunWithProgressAsync<T>(string title, Func<IProgress<ProgressUpdate>, CancellationToken, Task<T>> work, bool canCancel = true)
    {
        ArgumentNullException.ThrowIfNull(work);

        var dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        if (dispatcher.CheckAccess())
        {
            return RunWithProgressCoreAsync(title, work, canCancel);
        }

        return dispatcher.InvokeAsync(() => RunWithProgressCoreAsync(title, work, canCancel), DispatcherPriority.Normal).Task.Unwrap();
    }

    /// <summary>
    /// UI-thread part of <see cref="RunWithProgressAsync{T}"/>. The work is started here, so its synchronous
    /// prefix and every continuation run on the dispatcher; the dialog's modal loop pumps them while it is up.
    /// </summary>
    private async Task<T> RunWithProgressCoreAsync<T>(string title, Func<IProgress<ProgressUpdate>, CancellationToken, Task<T>> work, bool canCancel)
    {
        using var cancellation = new CancellationTokenSource();
        var dialog = new ProgressDialog();
        dialog.Present(title, canCancel);
        dialog.CancelRequested += (_, _) =>
        {
            _logger.LogInformation("'{Title}' cancelled by the user", title);
            cancellation.Cancel();
        };

        // Progress<T> captures the current (dispatcher) synchronization context, so reports from worker threads
        // land on the UI thread.
        var progress = new Progress<ProgressUpdate>(dialog.Report);

        Task<T> task;
        try
        {
            task = work(progress, cancellation.Token);
        }
        catch (Exception ex)
        {
            task = Task.FromException<T>(ex);
        }

        if (!task.IsCompleted)
        {
            CloseWhenDone(task, dialog);
            _logger.LogDebug("Progress dialog '{Title}' shown", title);
            ShowModal(dialog);
            _logger.LogDebug("Progress dialog '{Title}' closed", title);
        }

        return await task;
    }

    /// <summary>Closes the progress dialog on the UI thread once the work has finished, however it finished.</summary>
    private static async void CloseWhenDone<T>(Task<T> task, ProgressDialog dialog)
    {
        try
        {
            await task;
        }
        catch
        {
            // The caller observes the task's outcome; this continuation only closes the window.
        }

        dialog.Complete();
    }

    public Task<InstallPickerResult?> ShowInstallPickerAsync(InstallPlan plan, string? suggestedName = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return OnUiThreadAsync(() =>
        {
            var dialog = new InstallPickerDialog();
            dialog.Present(plan, suggestedName);
            ShowModal(dialog);
            _logger.LogDebug(
                "Install picker for {Source} closed ({Outcome})",
                plan.Source.DisplayName,
                dialog.Result is null ? "cancelled" : $"{dialog.Result.Selected.Count} set(s) as '{dialog.Result.Name}'");
            return dialog.Result;
        });
    }

    private Task<bool> ShowMessageAsync(MessageDialogOptions options) =>
        OnUiThreadAsync(() =>
        {
            var dialog = new MessageDialog();
            dialog.Present(options);
            ShowModal(dialog);
            _logger.LogDebug("{Kind} dialog '{Title}' closed ({Outcome})", options.Kind, options.Title, dialog.Confirmed ? "confirmed" : "dismissed");
            return dialog.Confirmed;
        });

    /// <summary>Shows <paramref name="dialog"/> modally over the active window, or centred on screen with a taskbar entry.</summary>
    private static void ShowModal(Window dialog) => ModalDialogs.ShowModal(dialog);

    /// <summary>The active visible window, else the main window when visible; null when nothing is on screen yet.</summary>
    private static Window? FindOwner(Window? exclude) => ModalDialogs.FindOwner(exclude);

    private static Task<T> OnUiThreadAsync<T>(Func<T> action)
    {
        var dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        if (dispatcher.CheckAccess())
        {
            // Already on the UI thread: ShowDialog pumps messages, so the UI stays responsive while we block here.
            try
            {
                return Task.FromResult(action());
            }
            catch (Exception ex)
            {
                return Task.FromException<T>(ex);
            }
        }

        return dispatcher.InvokeAsync(action, DispatcherPriority.Normal).Task;
    }
}
