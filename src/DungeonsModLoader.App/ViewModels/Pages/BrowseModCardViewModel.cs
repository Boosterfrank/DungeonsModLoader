using CommunityToolkit.Mvvm.ComponentModel;
using DungeonsModLoader.Core.Mods;
using DungeonsModLoader.Nexus.Api;
using DungeonsModLoader.Nexus.Updates;

namespace DungeonsModLoader.App.ViewModels.Pages;

/// <summary>One card of the Browse grid.</summary>
public sealed partial class BrowseModCardViewModel : ObservableObject
{
    public BrowseModCardViewModel(NexusMod mod)
    {
        Mod = mod;
        Apply(mod);
    }

    public NexusMod Mod { get; private set; }

    public long ModId => Mod.ModId;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _author = string.Empty;

    [ObservableProperty]
    private string _summary = string.Empty;

    [ObservableProperty]
    private string _statsText = string.Empty;

    [ObservableProperty]
    private string _updatedText = string.Empty;

    [ObservableProperty]
    private string? _version;

    /// <summary>Local file of the thumbnail once the cache has it; null shows the placeholder.</summary>
    [ObservableProperty]
    private string? _thumbnailPath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowInstalledBadge))]
    private bool _isInstalled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowInstalledBadge))]
    private bool _hasUpdate;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isAdult;

    /// <summary>"Installed" is shown only when there is no "Update" badge to show instead.</summary>
    public bool ShowInstalledBadge => IsInstalled && !HasUpdate;

    public string ThumbnailUrl => Mod.ThumbnailUrl ?? Mod.PictureUrl ?? string.Empty;

    public void Apply(NexusMod mod)
    {
        Mod = mod;
        Name = mod.Name;
        Author = string.IsNullOrWhiteSpace(mod.Author) ? mod.Uploader ?? "Unknown author" : mod.Author;
        Summary = mod.Summary;
        Version = mod.Version;
        StatsText = $"{Format.Count(mod.Endorsements)} endorsements · {Format.Count(mod.Downloads)} downloads";
        UpdatedText = "Updated " + Format.Relative(mod.UpdatedAt);
        IsAdult = mod.AdultContent;
    }

    /// <summary>Syncs the Installed / Update badges with the mod store and the update checker.</summary>
    public void RefreshState(IModService mods, IModUpdateChecker updates)
    {
        var installed = mods.Mods.FirstOrDefault(m => m.Entry.Source == ModSource.Nexus && m.Entry.NexusModId == ModId);
        IsInstalled = installed is not null;
        HasUpdate = installed is not null && updates.Updates.ContainsKey(installed.Entry.Id);
    }
}
