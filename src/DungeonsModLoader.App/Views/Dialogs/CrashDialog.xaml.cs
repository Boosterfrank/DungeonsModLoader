using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using DungeonsModLoader.Core;

namespace DungeonsModLoader.App.Views.Dialogs;

/// <summary>
/// Themed replacement for the stock "unhandled exception" MessageBox. Deliberately dependency-free (no DI, no
/// logger) so it still works when the thing that failed is the host itself; use <see cref="ShowFor"/>.
/// </summary>
public partial class CrashDialog : Window
{
    private AppPaths? _paths;

    public CrashDialog()
    {
        InitializeComponent();
    }

    /// <summary>True when the user chose "Continue"; false for "Close app" or any other way of dismissing the dialog.</summary>
    public bool ContinueChosen { get; private set; }

    /// <summary>
    /// Shows the dialog modally for <paramref name="exception"/> and returns whether the app should keep running.
    /// </summary>
    /// <param name="exception">The unhandled exception.</param>
    /// <param name="owner">Window to centre on; null (or a window without a handle yet) centres on the screen.</param>
    /// <param name="paths">Used by "Open logs folder".</param>
    /// <param name="canContinue">False hides "Continue" (e.g. the failure happened before the main window existed).</param>
    public static bool ShowFor(Exception exception, Window? owner, AppPaths paths, bool canContinue = true)
    {
        var dialog = new CrashDialog();
        dialog.Present(exception, paths, canContinue);

        if (owner is not null && !ReferenceEquals(owner, dialog) && new WindowInteropHelper(owner).Handle != IntPtr.Zero)
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
        return dialog.ContinueChosen;
    }

    /// <summary>Fills the dialog for <paramref name="exception"/> without showing it.</summary>
    public void Present(Exception exception, AppPaths paths, bool canContinue = true)
    {
        _paths = paths;

        MessageText.Text = canContinue
            ? $"{AppInfo.DisplayName} ran into an unexpected error. You can try to continue, but if the app misbehaves, close it and start it again."
            : $"{AppInfo.DisplayName} ran into an unexpected error while starting and needs to close. The details below help when reporting the problem.";

        DetailsBox.Text = BuildReport(exception);

        ContinueButton.Visibility = canContinue ? Visibility.Visible : Visibility.Collapsed;
        ContinueButton.IsCancel = canContinue;
        CloseAppButton.IsCancel = !canContinue;
    }

    private static string BuildReport(Exception exception)
    {
        var report = new StringBuilder();
        report.Append(AppInfo.DisplayName).Append(' ').Append(AppInfo.Version)
              .Append(" | ").Append(RuntimeInformation.OSDescription)
              .Append(" | ").Append(RuntimeInformation.FrameworkDescription)
              .Append(" | ").Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"))
              .AppendLine()
              .AppendLine();

        // ToString() covers type, message, stack trace and every inner exception.
        report.Append(exception);
        return report.ToString();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Activate();
        CloseAppButton.Focus();
    }

    private void OnHeaderMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // The button was released before the drag started; nothing to do.
        }
    }

    private void OnOpenLogsClick(object sender, RoutedEventArgs e)
    {
        // Direct shell call on purpose: the window service may be unavailable in a crash.
        var folder = _paths?.LogsDirectory;
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            return;
        }

        try
        {
            var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{fullPath}\"") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception)
        {
            // Explorer could not be started; the dialog itself must never fail.
        }
    }

    private void OnContinueClick(object sender, RoutedEventArgs e)
    {
        ContinueChosen = true;
        Close();
    }

    private void OnCloseAppClick(object sender, RoutedEventArgs e)
    {
        ContinueChosen = false;
        Close();
    }
}
