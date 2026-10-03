using System.Windows;
using System.Windows.Shell;
using DungeonsModLoader.App.ViewModels;
using DungeonsModLoader.App.Views.Shell;

namespace DungeonsModLoader.App.Views;

/// <summary>
/// Application shell window. The code-behind handles chrome concerns only (caption buttons, Win32 hooks);
/// everything else is bound to <see cref="MainViewModel"/>.
/// </summary>
public partial class MainWindow : Window
{
    private WindowChromeHelper? _chromeHelper;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Correct maximized bounds (work area, per monitor, DPI aware) + Windows 11 snap layouts on the maximize button.
        _chromeHelper = WindowChromeHelper.Attach(this, MaximizeButton);
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void OnMaximizeRestoreClick(object sender, RoutedEventArgs e) => WindowChromeHelper.ToggleMaximize(this);

    private void OnCloseClick(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);
}
