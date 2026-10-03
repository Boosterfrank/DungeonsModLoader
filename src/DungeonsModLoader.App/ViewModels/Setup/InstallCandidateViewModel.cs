using CommunityToolkit.Mvvm.ComponentModel;
using DungeonsModLoader.Core.Game;

namespace DungeonsModLoader.App.ViewModels.Setup;

/// <summary>A detected (or hand-picked) game installation shown as a selectable card in setup step 1.</summary>
public sealed partial class InstallCandidateViewModel : ObservableObject
{
    private readonly Action<InstallCandidateViewModel> _onSelected;

    public InstallCandidateViewModel(GameInstallation installation, Action<InstallCandidateViewModel> onSelected)
    {
        Installation = installation;
        _onSelected = onSelected;
    }

    public GameInstallation Installation { get; }

    public string Root => Installation.Root;

    public GameSource Source => Installation.Source;

    public bool IsSteam => Source == GameSource.Steam;

    public bool IsXbox => Source == GameSource.Xbox;

    public bool IsManual => Source == GameSource.Manual;

    public string SourceLabel => GameSourceLabels.For(Source);

    public string LaunchHint => GameSourceLabels.LaunchHintFor(Source);

    /// <summary>Bound two-way to the card's RadioButton; the group makes the cards mutually exclusive.</summary>
    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsSelectedChanged(bool value)
    {
        if (value)
        {
            _onSelected(this);
        }
    }
}
