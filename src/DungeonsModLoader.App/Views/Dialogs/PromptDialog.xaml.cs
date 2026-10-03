using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DungeonsModLoader.App.Views.Dialogs;

/// <summary>
/// Themed single-line text prompt. Dependency-free; <see cref="Services.DialogService"/> creates it, fills it
/// with <see cref="Present"/> and shows it modally. <see cref="Result"/> is the entered text, or null when cancelled.
/// </summary>
public partial class PromptDialog : Window
{
    public PromptDialog()
    {
        InitializeComponent();
    }

    /// <summary>The trimmed text when confirmed; null when cancelled.</summary>
    public string? Result { get; private set; }

    /// <summary>Fills the dialog without showing it.</summary>
    public void Present(string title, string message, string? initialValue, string confirmText)
    {
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        MessageText.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
        InputBox.Text = initialValue ?? string.Empty;
        ConfirmButton.Content = confirmText;
        UpdateConfirmState();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Activate();
        InputBox.Focus();
        InputBox.SelectAll();
    }

    private void OnInputTextChanged(object sender, TextChangedEventArgs e) => UpdateConfirmState();

    private void UpdateConfirmState() => ConfirmButton.IsEnabled = !string.IsNullOrWhiteSpace(InputBox.Text);

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

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(InputBox.Text))
        {
            return;
        }

        Result = InputBox.Text.Trim();
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        Result = null;
        DialogResult = false;
    }
}
