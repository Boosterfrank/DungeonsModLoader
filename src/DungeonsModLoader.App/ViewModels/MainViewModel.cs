using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DungeonsModLoader.App.ViewModels.Pages;

namespace DungeonsModLoader.App.ViewModels;

/// <summary>Shell view model: owns the four pages, the current selection and the Play action.</summary>
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

    /// <summary>The page shown in the content area. Never null; defaults to <see cref="Installed"/>.</summary>
    [ObservableProperty]
    private PageViewModel _currentPage;

    /// <summary>Transient message shown in the title bar (empty = hidden).</summary>
    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>Switches the content area to <paramref name="page"/> (sidebar items and Ctrl+1..4 use this).</summary>
    [RelayCommand]
    private void Navigate(PageViewModel? page)
    {
        if (page is not null)
        {
            CurrentPage = page;
        }
    }

    /// <summary>Launches the game. Stub until milestone 2 wires up Steam launching and the game-running lock.</summary>
    [RelayCommand]
    private void Play()
    {
        StatusText = "Game launching arrives in milestone 2";
    }
}
