using System.Windows;
using System.Windows.Shell;
using System.Windows.Threading;
using DungeonsModLoader.App.Services;
using DungeonsModLoader.App.ViewModels;
using DungeonsModLoader.App.Views.Shell;

namespace DungeonsModLoader.App.Views;

/// <summary>
/// Application shell window. The code-behind handles chrome concerns (caption buttons, Win32 hooks) and the
/// drag & drop plumbing (overlay + hand-over to the view model); everything else is bound to <see cref="MainViewModel"/>.
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private WindowChromeHelper? _chromeHelper;

    /// <summary>
    /// Nesting depth of the current drag: every element the cursor crosses raises its own Enter/Leave pair, so the
    /// overlay is only hidden once the count is back to zero and has stayed there past the paired Enter.
    /// </summary>
    private int _dragDepth;

    /// <summary>Whether the data of the current drag can be installed; decided on Enter, reused on every Over.</summary>
    private bool _dragAccepted;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.Preview.PropertyChanged += OnPreviewPropertyChanged;
    }

    // ----------------------------------------------------------------------------------------------------------
    // Picture viewer: the overlay takes keyboard focus when it opens (so Esc / arrows reach its bindings instead
    // of the page underneath) and gives it back when it closes.
    // ----------------------------------------------------------------------------------------------------------

    private void OnPreviewPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ImagePreviewViewModel.IsOpen))
        {
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (_viewModel.Preview.IsOpen)
            {
                PreviewOverlay.Focus();
            }
            else if (PreviewOverlay.IsKeyboardFocusWithin)
            {
                Focus();
            }
        });
    }

    /// <summary>A click on the dark backdrop (not on the picture or a button) closes the viewer.</summary>
    private void OnPreviewBackdropMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, PreviewOverlay) || ReferenceEquals(e.OriginalSource, PreviewBackdrop))
        {
            _viewModel.Preview.CloseCommand.Execute(null);
            e.Handled = true;
        }
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

    // ----------------------------------------------------------------------------------------------------------
    // Drag & drop install. The tunnelling events are handled (and marked handled) only for file drops, so text
    // drags over the search box still reach the TextBox, while file drags get one consistent answer everywhere.
    // ----------------------------------------------------------------------------------------------------------

    private void OnPreviewDragEnter(object sender, DragEventArgs e)
    {
        var paths = GetDroppedPaths(e.Data);
        if (paths is null)
        {
            return;
        }

        _dragDepth++;
        _dragAccepted = paths.Length > 0 && !_viewModel.IsGameRunning && InstallCoordinator.CanAcceptPaths(paths);
        ApplyDragFeedback(e);
    }

    private void OnPreviewDragOver(object sender, DragEventArgs e)
    {
        if (GetDroppedPaths(e.Data) is null)
        {
            return;
        }

        // The game may have started mid-drag; the paths themselves cannot change.
        _dragAccepted = _dragAccepted && !_viewModel.IsGameRunning;
        ApplyDragFeedback(e);
    }

    private void OnPreviewDragLeave(object sender, DragEventArgs e)
    {
        if (GetDroppedPaths(e.Data) is null)
        {
            return;
        }

        e.Handled = true;
        _dragDepth = Math.Max(0, _dragDepth - 1);

        // Leave(old child) and Enter(new child) arrive back to back; decide once both have been processed.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (_dragDepth == 0)
            {
                SetDropOverlay(false);
            }
        });
    }

    private async void OnPreviewDrop(object sender, DragEventArgs e)
    {
        var paths = GetDroppedPaths(e.Data);
        if (paths is null)
        {
            return;
        }

        e.Handled = true;
        _dragDepth = 0;
        SetDropOverlay(false);

        if (!_dragAccepted || paths.Length == 0 || _viewModel.IsGameRunning)
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        e.Effects = DragDropEffects.Copy;

        // Drop runs inside the source's DoDragDrop call (Explorer waits for it): return first, install afterwards,
        // otherwise the first modal dialog would freeze the source while it is open.
        await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.Background);
        await _viewModel.InstallFilesAsync(paths);
    }

    private void ApplyDragFeedback(DragEventArgs e)
    {
        e.Effects = _dragAccepted ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        SetDropOverlay(_dragAccepted);
    }

    private void SetDropOverlay(bool visible) =>
        DropOverlay.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The dropped file paths, or null when the data is not a file drop (text, other apps' formats).</summary>
    private static string[]? GetDroppedPaths(IDataObject data)
    {
        try
        {
            if (!data.GetDataPresent(DataFormats.FileDrop))
            {
                return null;
            }

            return data.GetData(DataFormats.FileDrop) as string[] ?? Array.Empty<string>();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            // Some sources advertise FileDrop but fail to deliver it; treat it as nothing to drop.
            return Array.Empty<string>();
        }
    }
}
