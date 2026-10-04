using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using DungeonsModLoader.App.Services;

namespace DungeonsModLoader.App.Views.Dialogs;

/// <summary>
/// Themed progress window for <see cref="Services.DialogService.RunWithProgressAsync{T}"/>. Dependency-free: the
/// service fills it with <see cref="Present"/>, feeds it through <see cref="Report"/> (on the UI thread) and ends
/// it with <see cref="Complete"/>. The user cannot close it (Alt+F4 is swallowed); Cancel / Esc only raise
/// <see cref="CancelRequested"/> and the dialog stays up until the work has actually stopped.
/// </summary>
public partial class ProgressDialog : Window
{
    private bool _canCancel;
    private bool _completed;
    private bool _cancelRequested;

    public ProgressDialog()
    {
        InitializeComponent();
    }

    /// <summary>Raised once when the user asks to cancel (button or Esc).</summary>
    public event EventHandler? CancelRequested;

    /// <summary>Fills the dialog without showing it.</summary>
    public void Present(string title, bool canCancel)
    {
        Title = title;
        TitleText.Text = title;
        _canCancel = canCancel;
        Footer.Visibility = canCancel ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Shows the current step. Must be called on the dialog's thread.</summary>
    public void Report(ProgressUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (_cancelRequested)
        {
            // "Cancelling..." stays until the work has stopped; late reports must not overwrite it.
            return;
        }

        if (!string.IsNullOrWhiteSpace(update.Message))
        {
            MessageText.Text = update.Message;
        }

        if (update.Fraction is { } fraction)
        {
            Bar.IsIndeterminate = false;
            Bar.Value = Math.Clamp(fraction, 0, 1) * 100;
        }
        else
        {
            Bar.IsIndeterminate = true;
        }
    }

    /// <summary>The work has finished (successfully or not): closes the dialog, now or as soon as it has loaded.</summary>
    public void Complete()
    {
        _completed = true;
        if (IsLoaded)
        {
            Close();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_completed)
        {
            // The work finished before the window was up; nothing to show.
            Close();
            return;
        }

        Activate();
        if (_canCancel)
        {
            CancelButton.Focus();
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // Only the service closes this window; Alt+F4 and the task bar must not.
        if (!_completed)
        {
            e.Cancel = true;
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            RequestCancel();
            e.Handled = true;
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => RequestCancel();

    private void RequestCancel()
    {
        if (!_canCancel || _cancelRequested || _completed)
        {
            return;
        }

        _cancelRequested = true;
        CancelButton.IsEnabled = false;
        MessageText.Text = "Cancelling...";
        Bar.IsIndeterminate = true;
        CancelRequested?.Invoke(this, EventArgs.Empty);
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
}
