using CommunityToolkit.Mvvm.ComponentModel;

namespace DungeonsModLoader.App.ViewModels.Setup;

/// <summary>One entry of the wizard's step list (left column): number, title, current / completed state.</summary>
public sealed partial class SetupStepItem : ObservableObject
{
    public SetupStepItem(SetupStep step, string title)
    {
        Step = step;
        Title = title;
        Number = (int)step + 1;
    }

    public SetupStep Step { get; }

    public int Number { get; }

    public string Title { get; }

    [ObservableProperty]
    private bool _isCurrent;

    [ObservableProperty]
    private bool _isCompleted;
}
