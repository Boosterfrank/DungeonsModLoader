using CommunityToolkit.Mvvm.ComponentModel;
using DungeonsModLoader.App.Services;
using DungeonsModLoader.Core.Install;

namespace DungeonsModLoader.App.ViewModels.Dialogs;

/// <summary>
/// State of the install picker: the mod name and one group of check-able file sets per
/// <see cref="InstallCandidate"/>. Shown when a package offers more than one file set (variants, options).
/// </summary>
public sealed partial class InstallPickerViewModel : ObservableObject
{
    public InstallPickerViewModel(InstallPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        Header = $"Install {plan.Source.DisplayName}";
        _name = plan.SuggestedName;
        PreserveStructure = plan.PreserveStructure;

        var defaults = new HashSet<ModFileSet>(plan.DefaultSelection);
        Groups = plan.Candidates
            .Select(candidate => new InstallPickerGroupViewModel(
                candidate.Name,
                candidate.FileSets.Select(set => new InstallPickerItemViewModel(set, defaults.Contains(set), OnSelectionChanged)).ToList()))
            .ToList();

        // Incomplete sets already show their warning beneath their row; do not repeat it in the summary list.
        var setWarnings = new HashSet<string>(plan.AllFileSets.Select(set => set.Warning).OfType<string>(), StringComparer.Ordinal);
        Warnings = plan.Warnings.Where(warning => !setWarnings.Contains(warning)).ToList();
    }

    /// <summary>"Install &lt;archive name&gt;".</summary>
    public string Header { get; }

    public IReadOnlyList<InstallPickerGroupViewModel> Groups { get; }

    /// <summary>True when the package ships data files, so its folder layout is kept on install.</summary>
    public bool PreserveStructure { get; }

    public IReadOnlyList<string> Warnings { get; }

    public bool HasWarnings => Warnings.Count > 0;

    /// <summary>Display name of the new mod (prefilled from the archive name).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    private string _name;

    /// <summary>At least one valid set is checked and the name is not blank.</summary>
    public bool CanInstall => !string.IsNullOrWhiteSpace(Name) && SelectedSets().Any();

    /// <summary>The user's choice, or null when nothing valid is selected or the name is blank.</summary>
    public InstallPickerResult? BuildResult()
    {
        var selected = SelectedSets().ToList();
        var name = Name.Trim();
        return selected.Count == 0 || name.Length == 0 ? null : new InstallPickerResult(selected, name);
    }

    private IEnumerable<ModFileSet> SelectedSets() =>
        Groups.SelectMany(group => group.Items).Where(item => item.IsSelected && item.IsValid).Select(item => item.Set);

    private void OnSelectionChanged() => OnPropertyChanged(nameof(CanInstall));
}

/// <summary>One folder of the package ("Main", "Option A") with its file sets.</summary>
public sealed class InstallPickerGroupViewModel
{
    public InstallPickerGroupViewModel(string name, IReadOnlyList<InstallPickerItemViewModel> items)
    {
        Name = name;
        Items = items;
    }

    public string Name { get; }

    public IReadOnlyList<InstallPickerItemViewModel> Items { get; }
}

/// <summary>One check-able file set. Invalid (incomplete) sets are disabled and carry their warning.</summary>
public sealed partial class InstallPickerItemViewModel : ObservableObject
{
    private readonly Action _selectionChanged;

    public InstallPickerItemViewModel(ModFileSet set, bool isSelected, Action selectionChanged)
    {
        Set = set;
        _selectionChanged = selectionChanged;
        _isSelected = isSelected && set.IsValid;
    }

    public ModFileSet Set { get; }

    /// <summary>Base name without the "_P" suffix.</summary>
    public string DisplayName => Set.DisplayName;

    /// <summary>Package-relative path of the set ("Option A/VarA_P").</summary>
    public string DisplayPath => Set.DisplayPath;

    public bool IsValid => Set.IsValid;

    /// <summary>Why the set cannot be installed; null for a valid set.</summary>
    public string? Warning => Set.Warning;

    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsSelectedChanged(bool value) => _selectionChanged();
}
