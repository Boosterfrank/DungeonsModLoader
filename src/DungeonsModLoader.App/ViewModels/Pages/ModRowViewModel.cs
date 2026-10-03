using CommunityToolkit.Mvvm.ComponentModel;
using DungeonsModLoader.Core.Mods;

namespace DungeonsModLoader.App.ViewModels.Pages;

/// <summary>
/// One row of the Installed list: a managed mod (<see cref="ModInfo"/>) or an unmanaged folder found in
/// <c>~mods</c>. The row never touches the disk: flipping <see cref="IsEnabled"/> from the UI hands the request to
/// the page view model, which performs the move and reports whether it succeeded; on failure the row reverts.
/// <see cref="Refresh"/> updates a row in place so the list does not flicker when the mod service changes.
/// </summary>
public sealed partial class ModRowViewModel : ObservableObject
{
    public const string UnknownAuthor = "Unknown author";
    public const string UnmanagedSubtitle = "Found in ~mods - not managed yet";
    public const string MissingSubtitle = "Folder not found in ~mods or the disabled folder";

    /// <summary>Performs the enable/disable move for a row; returns false when it failed (the row then reverts).</summary>
    public delegate Task<bool> ToggleHandler(ModRowViewModel row, bool enabled);

    private readonly ToggleHandler? _toggle;
    private bool _isEnabled;

    private ModRowViewModel(string key, ToggleHandler? toggle)
    {
        Key = key;
        _toggle = toggle;
    }

    /// <summary>Creates a row for a managed mod.</summary>
    public ModRowViewModel(ModInfo info, ToggleHandler toggle)
        : this(KeyFor(info.Entry.Id), toggle)
    {
        Id = info.Entry.Id;
        FolderName = info.Entry.FolderName;
        Refresh(info);
    }

    /// <summary>
    /// Creates a row for a folder inside <c>~mods</c> that no manifest entry owns. It carries the "Unmanaged" badge
    /// instead of a source badge, so <see cref="IsLocal"/> and <see cref="IsNexus"/> are both false.
    /// </summary>
    public static ModRowViewModel ForUnmanaged(string folderName, string? folderPath)
    {
        return new ModRowViewModel(KeyFor(folderName), toggle: null)
        {
            FolderName = folderName,
            DisplayName = folderName,
            FolderPath = folderPath,
            Author = UnknownAuthor,
            IsUnmanaged = true,
            SourceLabel = "Local",
            IsLocal = false,
        };
    }

    public static string KeyFor(Guid modId) => modId.ToString("N");

    public static string KeyFor(string unmanagedFolderName) => "folder:" + unmanagedFolderName;

    /// <summary>Stable identity used to match rows across refreshes (mod id, or the folder name for unmanaged rows).</summary>
    public string Key { get; }

    /// <summary>Manifest id; <see cref="Guid.Empty"/> for unmanaged rows.</summary>
    public Guid Id { get; }

    public string FolderName { get; private init; } = string.Empty;

    [ObservableProperty]
    private string _displayName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Subtitle))]
    private string _author = UnknownAuthor;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Subtitle))]
    private string? _version;

    [ObservableProperty]
    private string _sourceLabel = "Local";

    [ObservableProperty]
    private bool _isNexus;

    [ObservableProperty]
    private bool _isLocal = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Subtitle))]
    [NotifyPropertyChangedFor(nameof(CanToggle))]
    [NotifyPropertyChangedFor(nameof(ShowToggle))]
    private bool _isMissing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Subtitle))]
    [NotifyPropertyChangedFor(nameof(IsManaged))]
    [NotifyPropertyChangedFor(nameof(ShowToggle))]
    private bool _isUnmanaged;

    [ObservableProperty]
    private string? _folderPath;

    [ObservableProperty]
    private long? _nexusModId;

    /// <summary>True while this row's own move is in flight (its toggle is disabled).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanToggle))]
    private bool _isBusy;

    /// <summary>Set by the page while mod changes are locked (game running / bulk operation in progress).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanToggle))]
    private bool _isLocked;

    public bool IsManaged => !IsUnmanaged;

    /// <summary>The toggle is shown for managed rows whose folder exists.</summary>
    public bool ShowToggle => IsManaged && !IsMissing;

    public bool CanToggle => ShowToggle && !IsBusy && !IsLocked;

    /// <summary>
    /// Whether the mod folder is inside <c>~mods</c>. Setting it from the UI asks the page to move the folder; the
    /// value is applied optimistically and reverted when the move fails.
    /// </summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value)
            {
                return;
            }

            var previous = _isEnabled;
            _isEnabled = value;
            OnPropertyChanged();

            if (_toggle is not null)
            {
                _ = ToggleAsync(value, previous);
            }
        }
    }

    /// <summary>Second line of the row.</summary>
    public string Subtitle
    {
        get
        {
            if (IsUnmanaged)
            {
                return UnmanagedSubtitle;
            }

            if (IsMissing)
            {
                return MissingSubtitle;
            }

            return string.IsNullOrWhiteSpace(Version) ? $"by {Author}" : $"by {Author} · v{Version}";
        }
    }

    /// <summary>Updates the row from a fresh snapshot without triggering a move.</summary>
    public void Refresh(ModInfo info)
    {
        var entry = info.Entry;
        DisplayName = string.IsNullOrWhiteSpace(entry.DisplayName) ? entry.FolderName : entry.DisplayName;
        Author = string.IsNullOrWhiteSpace(entry.Author) ? UnknownAuthor : entry.Author.Trim();
        Version = string.IsNullOrWhiteSpace(entry.Version) ? null : entry.Version.Trim();
        IsNexus = entry.Source == ModSource.Nexus;
        IsLocal = !IsNexus;
        SourceLabel = IsNexus ? "Nexus" : "Local";
        NexusModId = entry.NexusModId;
        IsMissing = info.IsMissing;
        IsUnmanaged = false;
        FolderPath = info.FolderPath;

        // A move in flight owns the enabled state until it reports back; a stale snapshot must not flip the switch.
        if (!IsBusy)
        {
            SetEnabledSilently(info.IsEnabled);
        }
    }

    /// <summary>Sets <see cref="IsEnabled"/> without asking the page to move anything.</summary>
    public void SetEnabledSilently(bool enabled)
    {
        if (_isEnabled == enabled)
        {
            return;
        }

        _isEnabled = enabled;
        OnPropertyChanged(nameof(IsEnabled));
    }

    private async Task ToggleAsync(bool enabled, bool previous)
    {
        bool succeeded;
        try
        {
            succeeded = await _toggle!(this, enabled);
        }
        catch
        {
            // The page reports failures to the user; the row only needs to fall back to the last known state.
            succeeded = false;
        }

        if (!succeeded)
        {
            SetEnabledSilently(previous);
        }
    }
}
