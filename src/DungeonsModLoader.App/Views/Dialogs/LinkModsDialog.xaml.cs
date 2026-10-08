using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using DungeonsModLoader.App.ViewModels.Dialogs;

namespace DungeonsModLoader.App.Views.Dialogs;

/// <summary>
/// "Link mods to Nexus Mods" dialog. Shown modally by <see cref="Services.LinkModsPrompt"/>; the body is the shared
/// <see cref="LinkModsView"/> (also a step of the first-run wizard). The view model asks for the close; while links
/// are being written the window refuses to close.
/// </summary>
public partial class LinkModsDialog : Window
{
    /// <summary>The window never grows past this share of the work area; the list scrolls instead.</summary>
    private const double MaxScreenHeightShare = 0.8;

    private readonly LinkModsViewModel _viewModel;

    public LinkModsDialog(LinkModsViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        MaxHeight = Math.Max(420, SystemParameters.WorkArea.Height * MaxScreenHeightShare);
        viewModel.CloseRequested += OnCloseRequested;
        Closed += (_, _) => viewModel.CloseRequested -= OnCloseRequested;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => Activate();

    private void OnCloseRequested(object? sender, bool linked)
    {
        try
        {
            DialogResult = linked;
        }
        catch (InvalidOperationException)
        {
            Close();
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_viewModel.IsApplying)
        {
            // Links are being written; let that finish.
            e.Cancel = true;
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
            // Released before the drag started.
        }
    }
}
