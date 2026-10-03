using CommunityToolkit.Mvvm.ComponentModel;

namespace DungeonsModLoader.App.ViewModels.Pages;

/// <summary>Base class for the four sidebar pages.</summary>
public abstract partial class PageViewModel : ObservableObject
{
    /// <summary>Page title shown in the header and the sidebar.</summary>
    public abstract string Title { get; }
}
