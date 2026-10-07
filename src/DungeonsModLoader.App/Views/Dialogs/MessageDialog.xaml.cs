using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DungeonsModLoader.App.Services;

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

    /// <summary>A question with one button per <see cref="MessageDialogOptions.Choices"/> entry; <see cref="MessageDialog.ChosenId"/> says which.</summary>
    Choice,
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

    /// <summary>The buttons of a <see cref="MessageDialogKind.Choice"/> dialog, left to right.</summary>
    public IReadOnlyList<DialogChoice>? Choices { get; init; }

    /// <summary>
    /// Invoked by "Open logs folder"; the button is shown for errors with details when this is set. The dialog
    /// itself stays dependency-free, so the caller (the dialog service) supplies the shell action.
    /// </summary>
    public Action? OpenLogsFolder { get; init; }

    /// <summary>False makes the confirm button the only way out: Esc and closing the window are ignored (mandatory notices).</summary>
    public bool AllowDismiss { get; init; } = true;
}

/// <summary>
/// Themed replacement for <c>MessageBox</c>: info, error (with expandable details), confirm and multi-choice
/// dialogs. Dependency-free; <see cref="Services.DialogService"/> creates it, fills it with <see cref="Present"/>
/// and shows it modally. <see cref="Confirmed"/> is true when the primary action was chosen;
/// <see cref="ChosenId"/> names the chosen button of a choice dialog.
/// </summary>
public partial class MessageDialog : Window
{
    private Action? _openLogsFolder;
    private bool _focusCancel;
    private bool _isChoice;
    private bool _allowDismiss = true;
    private Button? _focusTarget;

    public MessageDialog()
    {
        InitializeComponent();
    }

    /// <summary>True when the confirm/OK button was pressed; false for cancel, Esc or closing the window.</summary>
    public bool Confirmed { get; private set; }

    /// <summary>The <see cref="DialogChoice.Id"/> of the chosen button; null when dismissed with Esc or closed.</summary>
    public string? ChosenId { get; private set; }

    /// <summary>Fills the dialog without showing it.</summary>
    public void Present(MessageDialogOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        Title = options.Title;
        TitleText.Text = options.Title;
        MessageText.Text = options.Message;
        _openLogsFolder = options.OpenLogsFolder;
        _allowDismiss = options.AllowDismiss;

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
                ConfirmButton.IsCancel = options.AllowDismiss; // Esc dismisses an info dialog like OK does (unless mandatory).
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

            case MessageDialogKind.Choice:
                SetIcon("?", "Brush.Accent.Light", "Brush.Accent.Faint");
                CancelButton.Visibility = Visibility.Collapsed;
                OpenLogsButton.Visibility = Visibility.Collapsed;
                ConfirmButton.Visibility = Visibility.Collapsed;
                ConfirmButton.IsDefault = false;
                ConfirmButton.IsCancel = false;
                _isChoice = true;
                AddChoiceButtons(options.Choices ?? Array.Empty<DialogChoice>());
                break;
        }
    }

    /// <summary>One button per choice, appended to the footer in order; the primary one is the default (Enter).</summary>
    private void AddChoiceButtons(IReadOnlyList<DialogChoice> choices)
    {
        if (choices.Count == 0)
        {
            throw new ArgumentException("A choice dialog needs at least one choice.", nameof(choices));
        }

        Button? lastButton = null;
        for (var i = 0; i < choices.Count; i++)
        {
            var choice = choices[i];
            var styleKey = choice.IsDestructive ? "Button.Danger" : choice.IsPrimary ? "Button.Primary" : "Button.Secondary";
            var button = new Button
            {
                Content = choice.Text,
                Tag = choice.Id,
                MinWidth = 100,
                Margin = new Thickness(i == 0 ? 0 : 8, 0, 0, 0),
                Style = (Style)FindResource(styleKey),
                IsDefault = choice.IsPrimary,
            };
            button.Click += OnChoiceClick;
            ActionsPanel.Children.Add(button);

            if (choice.IsPrimary && _focusTarget is null)
            {
                _focusTarget = button;
            }

            lastButton = button;
        }

        // No primary choice: focus the right-most button, which is the conventional place for the safe action.
        _focusTarget ??= lastButton;
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
        if (_focusTarget is not null)
        {
            _focusTarget.Focus();
        }
        else if (_focusCancel)
        {
            CancelButton.Focus();
        }
        else
        {
            ConfirmButton.Focus();
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // A mandatory notice (AllowDismiss = false) can only leave through its button: Alt+F4 and the like are ignored.
        if (!_allowDismiss && !Confirmed)
        {
            e.Cancel = true;
            return;
        }

        base.OnClosing(e);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        // Choice dialogs have no IsCancel button (every button is a real answer), so Esc is handled here: it
        // dismisses the dialog with no choice.
        if (_isChoice && e.Key == Key.Escape)
        {
            ChosenId = null;
            Confirmed = false;
            DialogResult = false;
            e.Handled = true;
            return;
        }

        base.OnPreviewKeyDown(e);
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

    private void OnChoiceClick(object sender, RoutedEventArgs e)
    {
        ChosenId = (sender as Button)?.Tag as string;
        Confirmed = ChosenId is not null;
        DialogResult = true;
    }
}
