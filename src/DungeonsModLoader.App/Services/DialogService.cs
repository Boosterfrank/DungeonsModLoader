using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using DungeonsModLoader.App.Views.Dialogs;
using DungeonsModLoader.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace DungeonsModLoader.App.Services;

/// <summary>
/// Default <see cref="IDialogService"/>: themed <see cref="MessageDialog"/> / <see cref="PromptDialog"/> windows
/// shown modally over the active window (or centred on screen when none is up yet), plus the system folder picker.
/// Calls from a non-UI thread are marshalled to the dispatcher; each method completes when its dialog closes.
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
    private static void ShowModal(Window dialog)
    {
        var owner = FindOwner(dialog);
        if (owner is not null)
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            dialog.ShowInTaskbar = false;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dialog.ShowInTaskbar = true;
        }

        dialog.ShowDialog();
    }

    /// <summary>The active visible window, else the main window when visible; null when nothing is on screen yet.</summary>
    private static Window? FindOwner(Window? exclude)
    {
        var app = Application.Current;
        if (app is null)
        {
            return null;
        }

        var active = app.Windows.OfType<Window>()
            .FirstOrDefault(w => w.IsActive && w.IsVisible && !ReferenceEquals(w, exclude) && HasHandle(w));
        if (active is not null)
        {
            return active;
        }

        var main = app.MainWindow;
        return main is { IsVisible: true } && !ReferenceEquals(main, exclude) && HasHandle(main) ? main : null;
    }

    private static bool HasHandle(Window window) => new WindowInteropHelper(window).Handle != IntPtr.Zero;

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
