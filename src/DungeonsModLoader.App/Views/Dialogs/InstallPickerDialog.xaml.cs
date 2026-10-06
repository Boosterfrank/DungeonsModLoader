using System.Windows;
using System.Windows.Input;
using DungeonsModLoader.App.Services;
using DungeonsModLoader.App.ViewModels.Dialogs;
using DungeonsModLoader.Core.Install;

namespace DungeonsModLoader.App.Views.Dialogs;

/// <summary>
/// Themed picker for packages that offer a choice of file sets. Dependency-free; <see cref="Services.DialogService"/>
/// creates it, fills it with <see cref="Present"/> and shows it modally. <see cref="Result"/> is the user's choice,
/// or null when cancelled.
/// </summary>
public partial class InstallPickerDialog : Window
{
    /// <summary>The window never grows past this share of the work area; the list scrolls instead.</summary>
    private const double MaxScreenHeightShare = 0.7;

    private InstallPickerViewModel? _viewModel;

    public InstallPickerDialog()
    {
        InitializeComponent();
        MaxHeight = Math.Max(320, SystemParameters.WorkArea.Height * MaxScreenHeightShare);
    }

    /// <summary>The selected file sets and mod name when confirmed; null when cancelled.</summary>
    public InstallPickerResult? Result { get; private set; }

    /// <summary>Fills the dialog without showing it.</summary>
    public void Present(InstallPlan plan, string? suggestedName = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        _viewModel = new InstallPickerViewModel(plan, suggestedName);
        DataContext = _viewModel;
        Title = _viewModel.Header;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Activate();
        NameBox.Focus();
        NameBox.SelectAll();
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

    private void OnInstallClick(object sender, RoutedEventArgs e)
    {
        var result = _viewModel?.BuildResult();
        if (result is null)
        {
            // Enter on a disabled Install button, or the selection emptied meanwhile: stay open.
            return;
        }

        Result = result;
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        Result = null;
        DialogResult = false;
    }
}
