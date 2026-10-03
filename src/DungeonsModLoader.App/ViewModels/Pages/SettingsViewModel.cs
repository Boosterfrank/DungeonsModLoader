using CommunityToolkit.Mvvm.Input;
using DungeonsModLoader.App.Services;
using DungeonsModLoader.Core;

namespace DungeonsModLoader.App.ViewModels.Pages;

public sealed partial class SettingsViewModel : PageViewModel
{
    private readonly IWindowService _windows;
    private readonly AppPaths _paths;

    public SettingsViewModel(IWindowService windows, AppPaths paths)
    {
        _windows = windows;
        _paths = paths;
    }

    public override string Title => "Settings";

    public string AppVersion => AppInfo.Version;

    [RelayCommand]
    private void OpenSwatches() => _windows.ShowSwatchWindow();

    [RelayCommand]
    private void OpenLogsFolder() => _windows.OpenFolder(_paths.LogsDirectory);

    [RelayCommand]
    private void OpenDataFolder() => _windows.OpenFolder(_paths.Root);
}
