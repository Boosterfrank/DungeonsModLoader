using CommunityToolkit.Mvvm.ComponentModel;
using DungeonsModLoader.Core.Profiles;

namespace DungeonsModLoader.App.ViewModels.Pages;

/// <summary>One profile card on the Profiles page (and one entry of the profile dropdown on the Installed page).</summary>
public sealed partial class ProfileRowViewModel : ObservableObject
{
    public ProfileRowViewModel(ProfileInfo info)
    {
        Name = info.Name;
        Refresh(info);
    }

    /// <summary>Stable identity across refreshes (profile names are unique, case-insensitively).</summary>
    public string Name { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDelete))]
    private bool _isActive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModCountText))]
    private int _modCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModCountText))]
    private int _missingCount;

    /// <summary>First few mod names, for the card ("Cool Mod, Shiny Armor, +3 more").</summary>
    [ObservableProperty]
    private string _modSummary = string.Empty;

    [ObservableProperty]
    private string _updatedText = string.Empty;

    /// <summary>The active profile cannot be deleted (switch first); the page also refuses while locked.</summary>
    public bool CanDelete => !IsActive;

    /// <summary>"3 mods" / "1 mod" / "No mods", plus " · 1 not installed" when the profile lists mods this PC lacks.</summary>
    public string ModCountText
    {
        get
        {
            var count = ModCount switch
            {
                0 => "No mods enabled",
                1 => "1 mod enabled",
                var n => $"{n} mods enabled",
            };
            return MissingCount > 0 ? $"{count} · {MissingCount} not installed" : count;
        }
    }

    public void Refresh(ProfileInfo info, int missingCount = 0)
    {
        Name = info.Name;
        OnPropertyChanged(nameof(Name));
        IsActive = info.IsActive;
        ModCount = info.ModCount;
        MissingCount = missingCount;
        ModSummary = Summarize(info.Mods.Select(m => m.DisplayName).ToList());
        UpdatedText = "Updated " + Describe(info.UpdatedAt);
    }

    private static string Summarize(IReadOnlyList<string> names)
    {
        const int shown = 3;
        if (names.Count == 0)
        {
            return "Every mod is disabled in this profile.";
        }

        var head = string.Join(", ", names.Take(shown));
        return names.Count > shown ? $"{head}, +{names.Count - shown} more" : head;
    }

    private static string Describe(DateTimeOffset when)
    {
        var age = DateTimeOffset.UtcNow - when;
        if (age < TimeSpan.FromMinutes(1))
        {
            return "just now";
        }

        if (age < TimeSpan.FromHours(1))
        {
            var minutes = (int)age.TotalMinutes;
            return minutes == 1 ? "1 minute ago" : $"{minutes} minutes ago";
        }

        if (age < TimeSpan.FromDays(1))
        {
            var hours = (int)age.TotalHours;
            return hours == 1 ? "1 hour ago" : $"{hours} hours ago";
        }

        if (age < TimeSpan.FromDays(7))
        {
            var days = (int)age.TotalDays;
            return days == 1 ? "yesterday" : $"{days} days ago";
        }

        return when.ToLocalTime().ToString("d");
    }
}
