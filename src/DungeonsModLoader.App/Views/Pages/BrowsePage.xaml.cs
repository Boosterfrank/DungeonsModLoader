using System.Windows;
using System.Windows.Controls;
using DungeonsModLoader.App.ViewModels.Pages;

namespace DungeonsModLoader.App.Views.Pages;

/// <summary>
/// Browse page. DataContext is the <c>BrowseViewModel</c> (set by the shell's page DataTemplate). The code-behind
/// triggers the lazy first load and scrolls the grid back to the top when another page of results is shown.
/// </summary>
public partial class BrowsePage : UserControl
{
    public BrowsePage()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private BrowseViewModel? ViewModel => DataContext as BrowseViewModel;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is BrowseViewModel old)
        {
            old.PageChanged -= OnPageChanged;
        }

        if (e.NewValue is BrowseViewModel vm)
        {
            vm.PageChanged += OnPageChanged;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => ViewModel?.EnsureLoaded();

    private void OnPageChanged(object? sender, EventArgs e) => CardsScroller.ScrollToTop();
}
