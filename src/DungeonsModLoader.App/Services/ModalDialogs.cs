using System.Windows;
using System.Windows.Interop;

namespace DungeonsModLoader.App.Services;

/// <summary>Shows a themed dialog window modally over the right owner; shared by the dialog services.</summary>
internal static class ModalDialogs
{
    /// <summary>Shows <paramref name="dialog"/> modally over the active window, or centred on screen with a taskbar entry when no window is up.</summary>
    public static bool? ShowModal(Window dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);

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

        return dialog.ShowDialog();
    }

    /// <summary>The active visible window, else the main window when visible; null when nothing is on screen yet.</summary>
    public static Window? FindOwner(Window? exclude)
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
}
