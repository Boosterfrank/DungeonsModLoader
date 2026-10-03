using CommunityToolkit.Mvvm.ComponentModel;
using DungeonsModLoader.App.ViewModels.Pages;

namespace DungeonsModLoader.App.ViewModels;

/// <summary>Shell view model: owns the four pages and the current selection.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    public MainViewModel(
        InstalledViewModel installed,
        BrowseViewModel browse,
        ProfilesViewModel profiles,
        SettingsViewModel settings)
    {
        Installed = installed;
        Browse = browse;
        Profiles = profiles;
        Settings = settings;
        CurrentPage = installed;
    }

    public InstalledViewModel Installed { get; }
    public BrowseViewModel Browse { get; }
    public ProfilesViewModel Profiles { get; }
    public SettingsViewModel Settings { get; }

    [ObservableProperty]
    private PageViewModel _currentPage;
}
