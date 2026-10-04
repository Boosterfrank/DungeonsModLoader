using System.ComponentModel;
using System.Windows;
using System.Windows.Shell;
using DungeonsModLoader.App.ViewModels.Setup;
using DungeonsModLoader.App.Views.Shell;

namespace DungeonsModLoader.App.Views.Setup;

/// <summary>
/// First-run setup window. Shown modally by <see cref="App"/> before the main window exists; the dialog result
/// is true when setup finished and false when the user exited it. The code-behind owns chrome concerns only
/// (caption buttons, Win32 hooks) and the window-close handshake with <see cref="SetupViewModel"/>.
/// </summary>
public partial class SetupWindow : Window
{
    private readonly SetupViewModel _viewModel;
    private WindowChromeHelper? _chromeHelper;
    private bool _closeRequested;

    public SetupWindow(SetupViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.CloseRequested += OnCloseRequested;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Correct maximized bounds (work area, per monitor, DPI aware) + Windows 11 snap layouts on the maximize button.
        _chromeHelper = WindowChromeHelper.Attach(this, MaximizeButton);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Activate();
        await _viewModel.StartAsync();
    }

    private void OnCloseRequested(object? sender, bool result)
    {
        // Never run from inside OnClosing: the view model asks for confirmation first, and that confirmation is
        // started after the Closing handler has returned (see OnClosing), so setting DialogResult here closes
        // the window through the normal path.
        _closeRequested = true;
        try
        {
            DialogResult = result;
        }
        catch (InvalidOperationException)
        {
            // Not shown with ShowDialog (should not happen); close plainly instead.
            Close();
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closeRequested)
        {
            return;
        }

        // The X button / Alt+F4: keep the window open for now and let the wizard decide (it asks for confirmation
        // and may be busy with file work). The command must run AFTER this handler returns: a modal confirmation
        // inside Closing, followed by DialogResult = false, would re-enter the close pipeline and wedge the window.
        e.Cancel = true;
        Dispatcher.BeginInvoke(() =>
        {
            if (!_closeRequested && _viewModel.CancelCommand.CanExecute(null))
            {
                _viewModel.CancelCommand.Execute(null);
            }
        });
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _viewModel.CloseRequested -= OnCloseRequested;
        _viewModel.Dispose();
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void OnMaximizeRestoreClick(object sender, RoutedEventArgs e) => WindowChromeHelper.ToggleMaximize(this);

    private void OnCloseClick(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);
}
