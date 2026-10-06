using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.Input;
using DungeonsModLoader.App.Services;
using DungeonsModLoader.App.ViewModels.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace DungeonsModLoader.App.Views.Pages;

/// <summary>
/// Browse page. DataContext is the <c>BrowseViewModel</c> (set by the shell's page DataTemplate). The code-behind
/// triggers the lazy first load and the "load more when scrolled to the bottom" behaviour.
/// </summary>
public partial class BrowsePage : UserControl
{
    /// <summary>Opens a requirement's Nexus page (the hyperlink in the detail panel binds to this).</summary>
    public IRelayCommand<BrowseRequirementViewModel?> OpenRequirementCommand { get; }

    public BrowsePage()
    {
        InitializeComponent();
        OpenRequirementCommand = new RelayCommand<BrowseRequirementViewModel?>(OpenRequirement);
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

    private static void OpenRequirement(BrowseRequirementViewModel? requirement)
    {
        if (requirement is null)
        {
            return;
        }

        var url = requirement.Requirement.Url;
        if (string.IsNullOrWhiteSpace(url) && requirement.Requirement.ModId is { } id)
        {
            url = Nexus.NexusConstants.ModPageUrl(id);
        }

        if (!string.IsNullOrWhiteSpace(url))
        {
            App.Services.GetRequiredService<IWindowService>().OpenUrl(url);
        }
    }
}
