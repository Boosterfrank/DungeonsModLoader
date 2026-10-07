using System.Windows;
using System.Windows.Controls;
using DungeonsModLoader.App.ViewModels.Pages;

namespace DungeonsModLoader.App.Views.Pages;

/// <summary>
/// Browse page. DataContext is the <c>BrowseViewModel</c> (set by the shell's page DataTemplate). The code-behind
/// triggers the lazy first load and the "load more when scrolled to the bottom" behaviour of the card grid.
/// </summary>
public partial class BrowsePage : UserControl
{
    public BrowsePage()
    {
        InitializeComponent();
    }

    private BrowseViewModel? ViewModel => DataContext as BrowseViewModel;

    private void OnLoaded(object sender, RoutedEventArgs e) => ViewModel?.EnsureLoaded();

    private void OnCardsScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (ViewModel is not { } vm || e.ExtentHeight <= 0)
        {
            return;
        }

        // Within one card height of the bottom: fetch the next page.
        if (e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 240 && vm.LoadMoreCommand.CanExecute(null))
        {
            vm.LoadMoreCommand.Execute(null);
        }
    }
}
