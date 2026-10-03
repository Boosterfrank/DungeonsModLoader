using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace DungeonsModLoader.App.Views.Dialogs;

/// <summary>What a <see cref="MessageDialog"/> is for; decides the icon and the footer buttons.</summary>
public enum MessageDialogKind
{
    /// <summary>Neutral notice: one "OK" button.</summary>
    Info,

    /// <summary>Something failed: "OK" plus an optional details block and "Open logs folder".</summary>
    Error,

    /// <summary>A question: cancel + confirm buttons; the dialog result says which one was chosen.</summary>
    Confirm,
}

/// <summary>Everything <see cref="MessageDialog.Present"/> needs to fill the dialog.</summary>
public sealed class MessageDialogOptions
{
    public MessageDialogKind Kind { get; init; } = MessageDialogKind.Info;
    public required string Title { get; init; }
    public required string Message { get; init; }

    /// <summary>Technical details shown behind the "Details" disclosure (errors only).</summary>
    public string? Details { get; init; }

    public string ConfirmText { get; init; } = "OK";
    public string CancelText { get; init; } = "Cancel";

    /// <summary>Renders the confirm button in the danger style (and focuses cancel, so Enter is the safe choice).</summary>
    public bool IsDestructive { get; init; }

    /// <summary>
    /// Invoked by "Open logs folder"; the button is shown for errors with details when this is set. The dialog
    /// itself stays dependency-free, so the caller (the dialog service) supplies the shell action.
    /// </summary>
    public Action? OpenLogsFolder { get; init; }
}

/// <summary>
/// Themed replacement for <c>MessageBox</c>: info, error (with expandable details) and confirm dialogs.
/// Dependency-free; <see cref="Services.DialogService"/> creates it, fills it with <see cref="Present"/>
/// and shows it modally. <see cref="Confirmed"/> is true when the primary action was chosen.
/// </summary>
public partial class MessageDialog : Window
{
    private Action? _openLogsFolder;
    private bool _focusCancel;

    public MessageDialog()
    {
        InitializeComponent();
    }

    /// <summary>True when the confirm/OK button was pressed; false for cancel, Esc or closing the window.</summary>
    public bool Confirmed { get; private set; }

    /// <summary>Fills the dialog without showing it.</summary>
    public void Present(MessageDialogOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        Title = options.Title;
        TitleText.Text = options.Title;
        MessageText.Text = options.Message;
        _openLogsFolder = options.OpenLogsFolder;

        var hasDetails = !string.IsNullOrWhiteSpace(options.Details);
        DetailsToggle.Visibility = hasDetails ? Visibility.Visible : Visibility.Collapsed;
        DetailsBox.Text = options.Details ?? string.Empty;

        ConfirmButton.Content = options.ConfirmText;
        CancelButton.Content = options.CancelText;

        switch (options.Kind)
        {
            case MessageDialogKind.Info:
                SetIcon("i", "Brush.Cyan", "Brush.Cyan.Faint");
                CancelButton.Visibility = Visibility.Collapsed;
                OpenLogsButton.Visibility = Visibility.Collapsed;
                ConfirmButton.IsCancel = true; // Esc dismisses an info dialog like OK does.
                break;

            case MessageDialogKind.Error:
                SetIcon("!", "Brush.Danger", "Brush.Danger.Faint");
                CancelButton.Visibility = Visibility.Collapsed;
                OpenLogsButton.Visibility = hasDetails && _openLogsFolder is not null ? Visibility.Visible : Visibility.Collapsed;
                ConfirmButton.IsCancel = true;
                break;

            case MessageDialogKind.Confirm:
                if (options.IsDestructive)
                {
                    SetIcon("!", "Brush.Danger", "Brush.Danger.Faint");
                    ConfirmButton.Style = (Style)FindResource("Button.Danger");
                }
                else
                {
                    SetIcon("?", "Brush.Accent.Light", "Brush.Accent.Faint");
                }

                CancelButton.Visibility = Visibility.Visible;
                OpenLogsButton.Visibility = Visibility.Collapsed;
                ConfirmButton.IsCancel = false;
                _focusCancel = options.IsDestructive;
                break;
        }
    }

    private void SetIcon(string glyph, string foregroundKey, string backgroundKey)
    {
        IconGlyph.Text = glyph;
        var foreground = (Brush)FindResource(foregroundKey);
        IconGlyph.Foreground = foreground;
        IconBox.BorderBrush = foreground;
        IconBox.Background = (Brush)FindResource(backgroundKey);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Activate();
        if (_focusCancel)
        {
            CancelButton.Focus();
        }
        else
        {
            ConfirmButton.Focus();
        }
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
        try
        {
            _openLogsFolder?.Invoke();
        }
        catch (Exception)
        {
            // The shell action failed (it logs on its own); the dialog itself must never fail over it.
        }
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        Confirmed = true;
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        Confirmed = false;
        DialogResult = false;
    }
}
