using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DungeonsModLoader.App.Services;
using DungeonsModLoader.Core.Mods;
using DungeonsModLoader.Nexus.Api;
using DungeonsModLoader.Nexus.Auth;
using DungeonsModLoader.Nexus.Updates;
using Microsoft.Extensions.Logging;

namespace DungeonsModLoader.App.ViewModels.Pages;

/// <summary>A file of the mod on the detail page, with its Install / Update / Reinstall button.</summary>
public sealed partial class BrowseFileViewModel : ObservableObject
{
    public BrowseFileViewModel(NexusFile file)
    {
        File = file;
        SizeText = Format.Size(file.SizeBytes);
        DateText = Format.Relative(file.UploadedAt);
        Description = Nexus.Text.BbCode.ToPlainText(file.Description);
    }

    public NexusFile File { get; }

    public string Name => File.Name;

    public string Version => string.IsNullOrWhiteSpace(File.Version) ? "" : "v" + File.Version;

    public string SizeText { get; }

    public string DateText { get; }

    public string Description { get; }

    public bool HasDescription => Description.Length > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActionText))]
    private bool _isInstalledFile;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActionText))]
    private bool _isUpdateTarget;

    public string ActionText => IsUpdateTarget ? "Update" : IsInstalledFile ? "Reinstall" : "Install";
}

/// <summary>Files of one category (Main / Update / Optional / ...).</summary>
public sealed class BrowseFileGroupViewModel
{
    public BrowseFileGroupViewModel(string title, IReadOnlyList<BrowseFileViewModel> files)
    {
        Title = title;
        Files = files;
    }

    public string Title { get; }

    public IReadOnlyList<BrowseFileViewModel> Files { get; }
}

/// <summary>A mod this mod requires, with whether it is installed / enabled here.</summary>
public sealed partial class BrowseRequirementViewModel : ObservableObject
{
    public BrowseRequirementViewModel(NexusRequirement requirement)
    {
        Requirement = requirement;
    }

    public NexusRequirement Requirement { get; }

    public string Name => Requirement.Name;

    public string? Notes => Requirement.Notes;

    public bool HasNotes => !string.IsNullOrWhiteSpace(Requirement.Notes);

    public bool IsExternal => Requirement.IsExternal;

    /// <summary>Nexus requirements open inside the app; off-site ones open in the browser.</summary>
    public string OpenHint => Requirement.ModId is not null ? "Show this mod" : "Open in your browser";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(IsSatisfied), nameof(IsWarning))]
    private bool _isInstalled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(IsSatisfied), nameof(IsWarning))]
    private bool _isEnabled;

    public bool IsSatisfied => IsExternal || (IsInstalled && IsEnabled);

    public bool IsWarning => !IsSatisfied;

    public string StatusText => IsExternal
        ? "External"
        : IsInstalled ? (IsEnabled ? "Installed" : "Installed, disabled") : "Not installed";

    public void RefreshState(IModService mods)
    {
        if (Requirement.ModId is not { } id)
        {
            return;
        }

        var installed = mods.Mods.FirstOrDefault(m => m.Entry.Source == ModSource.Nexus && m.Entry.NexusModId == id);
        IsInstalled = installed is not null;
        IsEnabled = installed?.IsEnabled == true;
    }
}

/// <summary>Sections of the detail page.</summary>
public enum DetailTab
{
    Description,
    Files,
    Requirements,
}

/// <summary>
/// The full-page detail of a mod on the Browse page: hero picture, title and actions, then Description / Files /
/// Requirements tabs. Loads its parts independently so one failing request does not blank the others.
/// </summary>
public sealed partial class BrowseDetailViewModel : ObservableObject
{
    private readonly INexusApiClient _client;
    private readonly INexusSession _session;
    private readonly IModService _mods;
    private readonly IModUpdateChecker _updates;
    private readonly IInstallCoordinator _installs;
    private readonly IWindowService _windows;
    private readonly IThumbnailCache _thumbnails;
    private readonly ImagePreviewViewModel _preview;
    private readonly Func<long, Task> _openMod;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _lifetime = new();
    private NexusModUpdate? _pendingUpdate;

    /// <param name="openMod">Opens another mod's detail page (requirements link to it).</param>
    public BrowseDetailViewModel(
        NexusMod mod,
        INexusApiClient client,
        INexusSession session,
        IModService mods,
        IModUpdateChecker updates,
        IInstallCoordinator installs,
        IWindowService windows,
        IThumbnailCache thumbnails,
        ImagePreviewViewModel preview,
        Func<long, Task> openMod,
        ILogger logger)
    {
        Mod = mod;
        _client = client;
        _session = session;
        _mods = mods;
        _updates = updates;
        _installs = installs;
        _windows = windows;
        _thumbnails = thumbnails;
        _preview = preview;
        _openMod = openMod;
        _logger = logger;

        Author = string.IsNullOrWhiteSpace(mod.Author) ? mod.Uploader ?? "Unknown author" : mod.Author;
        StatsText = $"{Format.Count(mod.Endorsements)} endorsements · {Format.Count(mod.Downloads)} downloads · updated {Format.Relative(mod.UpdatedAt)}";
        Description = mod.Description;
        RefreshState();
    }

    public NexusMod Mod { get; private set; }

    public string Name => Mod.Name;

    public string Author { get; }

    public string StatsText { get; }

    public string? Category => Mod.Category;

    public string VersionText => string.IsNullOrWhiteSpace(Mod.Version) ? string.Empty : "v" + Mod.Version;

    public ObservableCollection<BrowseFileGroupViewModel> FileGroups { get; } = new();

    public ObservableCollection<BrowseRequirementViewModel> Requirements { get; } = new();

    // ------------------------------------------------------------------------------------------------------
    // Tabs
    // ------------------------------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDescriptionTab), nameof(IsFilesTab), nameof(IsRequirementsTab))]
    private DetailTab _tab = DetailTab.Description;

    public bool IsDescriptionTab
    {
        get => Tab == DetailTab.Description;
        set => SelectTab(value, DetailTab.Description);
    }

    public bool IsFilesTab
    {
        get => Tab == DetailTab.Files;
        set => SelectTab(value, DetailTab.Files);
    }

    public bool IsRequirementsTab
    {
        get => Tab == DetailTab.Requirements;
        set => SelectTab(value, DetailTab.Requirements);
    }

    private void SelectTab(bool selected, DetailTab tab)
    {
        if (selected)
        {
            Tab = tab;
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilesTabTitle))]
    private int _fileCount;

    public string FilesTabTitle => FileCount == 0 ? "Files" : $"Files ({FileCount})";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRequirements), nameof(RequirementsTabTitle))]
    private int _requirementCount;

    public bool HasRequirements => RequirementCount > 0;

    public string RequirementsTabTitle => RequirementCount == 0 ? "Requirements" : $"Requirements ({RequirementCount})";

    // ------------------------------------------------------------------------------------------------------
    // Content state
    // ------------------------------------------------------------------------------------------------------

    [ObservableProperty]
    private string? _description;

    [ObservableProperty]
    private bool _isLoadingDescription;

    [ObservableProperty]
    private bool _isLoadingFiles;

    [ObservableProperty]
    private bool _isLoadingRequirements;

    [ObservableProperty]
    private string? _filesError;

    [ObservableProperty]
    private string? _descriptionError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPicture))]
    private string? _picturePath;

    public bool HasPicture => PicturePath is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstalledText), nameof(ShowInstalled), nameof(ShowPrimaryAction), nameof(PrimaryActionText), nameof(PrimaryActionToolTip))]
    private bool _isInstalled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstalledText))]
    private string? _installedVersion;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstalledText), nameof(ShowInstalled), nameof(ShowPrimaryAction), nameof(PrimaryActionText), nameof(PrimaryActionToolTip))]
    private bool _hasUpdate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PrimaryActionToolTip))]
    private string? _updateVersion;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPrimaryAction))]
    private bool _hasMainFile;

    /// <summary>What the download will do for this account (no key / free member); null when nothing to say.</summary>
    [ObservableProperty]
    private string? _downloadHint;

    public bool ShowInstalled => IsInstalled;

    public string InstalledText => !IsInstalled
        ? string.Empty
        : HasUpdate
            ? $"Installed {InstalledVersion ?? ""} · update available".Replace("  ", " ")
            : $"Installed {InstalledVersion ?? ""}".TrimEnd();

    /// <summary>The one gold button: "Install" for new mods, "Update" when a newer file is known; hidden when installed and current.</summary>
    public bool ShowPrimaryAction => HasUpdate || (!IsInstalled && HasMainFile);

    public string PrimaryActionText => HasUpdate ? "Update" : "Install";

    public string PrimaryActionToolTip => HasUpdate
        ? $"Download {UpdateVersion ?? "the newer file"} and replace the installed files (name, state and profiles are kept)"
        : "Download and install the main file";

    // ------------------------------------------------------------------------------------------------------
    // Loading
    // ------------------------------------------------------------------------------------------------------

    /// <summary>Loads picture, description, files and requirements; call once after construction.</summary>
    public async Task LoadAsync()
    {
        var token = _lifetime.Token;
        var picture = LoadPictureAsync(token);
        var description = LoadDescriptionAsync(token);
        var files = LoadFilesAsync(token);
        var requirements = LoadRequirementsAsync(token);
        await Task.WhenAll(picture, description, files, requirements);
    }

    private async Task LoadPictureAsync(CancellationToken token)
    {
        try
        {
            PicturePath = await _thumbnails.GetFileAsync(Mod.PictureUrl ?? Mod.ThumbnailUrl, token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task LoadDescriptionAsync(CancellationToken token)
    {
        if (!string.IsNullOrWhiteSpace(Description))
        {
            return;
        }

        IsLoadingDescription = true;
        try
        {
            var full = await _client.GetModAsync(Mod.ModId, cancellationToken: token);
            Mod = Mod.WithDescription(full.Description);
            Description = string.IsNullOrWhiteSpace(full.Description) ? Mod.Summary : full.Description;
        }
        catch (OperationCanceledException)
        {
        }
        catch (NexusException ex)
        {
            _logger.LogWarning("Description of mod {Mod} could not be loaded: {Message}", Mod.ModId, ex.Message);
            DescriptionError = ex.Message;
            Description = Mod.Summary;
        }
        finally
        {
            IsLoadingDescription = false;
        }
    }

    private async Task LoadFilesAsync(CancellationToken token)
    {
        IsLoadingFiles = true;
        try
        {
            var list = await _client.GetFilesAsync(Mod.ModId, cancellationToken: token);
            FileGroups.Clear();
            foreach (var group in list.Files
                         .Where(f => f.IsDownloadable)
                         .GroupBy(f => f.Category)
                         .OrderBy(g => Order(g.Key)))
            {
                var files = group.OrderByDescending(f => f.UploadedAt).Select(f => new BrowseFileViewModel(f)).ToList();
                FileGroups.Add(new BrowseFileGroupViewModel(Title(group.Key), files));
            }

            FileCount = FileGroups.Sum(g => g.Files.Count);
            FilesError = FileCount > 0 ? null : "This mod has no downloadable files right now.";
            RefreshState();
        }
        catch (OperationCanceledException)
        {
        }
        catch (NexusException ex)
        {
            _logger.LogWarning("Files of mod {Mod} could not be loaded: {Message}", Mod.ModId, ex.Message);
            FilesError = ex.Message;
        }
        finally
        {
            IsLoadingFiles = false;
        }
    }

    private async Task LoadRequirementsAsync(CancellationToken token)
    {
        IsLoadingRequirements = true;
        try
        {
            var requirements = await _client.GetRequirementsAsync(Mod.ModId, cancellationToken: token);
            Requirements.Clear();
            foreach (var requirement in requirements)
            {
                var row = new BrowseRequirementViewModel(requirement);
                row.RefreshState(_mods);
                Requirements.Add(row);
            }

            RequirementCount = Requirements.Count;
        }
        catch (OperationCanceledException)
        {
        }
        catch (NexusException ex)
        {
            _logger.LogDebug("Requirements of mod {Mod} could not be loaded: {Message}", Mod.ModId, ex.Message);
        }
        finally
        {
            IsLoadingRequirements = false;
        }
    }

    /// <summary>Syncs Installed / Update state from the mod store, the update checker and the session.</summary>
    public void RefreshState()
    {
        var installed = _mods.Mods.FirstOrDefault(m => m.Entry.Source == ModSource.Nexus && m.Entry.NexusModId == Mod.ModId);
        IsInstalled = installed is not null;
        InstalledVersion = installed?.Entry.Version is { Length: > 0 } v ? "v" + v : null;
        _pendingUpdate = installed is not null && _updates.Updates.TryGetValue(installed.Entry.Id, out var pending) ? pending : null;
        HasUpdate = _pendingUpdate is not null;
        UpdateVersion = _pendingUpdate?.NewVersion;
        HasMainFile = FileGroups.SelectMany(g => g.Files).Any(f => f.File.Category == NexusFileCategory.Main);

        foreach (var file in FileGroups.SelectMany(g => g.Files))
        {
            file.IsInstalledFile = installed?.Entry.NexusFileId == file.File.FileId;
            file.IsUpdateTarget = _pendingUpdate?.NewFile.FileId == file.File.FileId;
        }

        foreach (var requirement in Requirements)
        {
            requirement.RefreshState(_mods);
        }

        DownloadHint = !_session.HasApiKey
            ? "Downloads use a Nexus Mods account. Click Install and you will be guided through connecting yours (about a minute)."
            : !_session.IsPremium && !Mod.DirectDownloadEnabled
                ? "Free account: Install opens the file's download page on Nexus Mods. Click \"Slow download\" there and the file comes back here on its own."
                : null;
    }

    // ------------------------------------------------------------------------------------------------------
    // Actions
    // ------------------------------------------------------------------------------------------------------

    [RelayCommand]
    private void OpenOnNexus() => _windows.OpenUrl(Mod.PageUrl);

    /// <summary>The gold button: installs the main file, or applies the known update.</summary>
    [RelayCommand]
    private async Task PrimaryActionAsync()
    {
        if (_pendingUpdate is { } update)
        {
            _logger.LogInformation("Update requested from the detail page: mod {Mod} -> {Version}", Mod.ModId, update.NewVersion);
            await _installs.UpdateFromNexusAsync(update);
            RefreshState();
            return;
        }

        await InstallMainAsync();
    }

    [RelayCommand]
    private async Task InstallFileAsync(BrowseFileViewModel? file)
    {
        if (file is null)
        {
            return;
        }

        _logger.LogInformation("Install requested from Browse: mod {Mod} file {File} ({Name} {Version})", Mod.ModId, file.File.FileId, file.File.Name, file.File.Version);
        await _installs.InstallFromNexusAsync(Mod, file.File);
        RefreshState();
    }

    /// <summary>Installs the primary (or newest main) file.</summary>
    private async Task InstallMainAsync()
    {
        var main = FileGroups.SelectMany(g => g.Files).Select(f => f.File)
            .Where(f => f.Category == NexusFileCategory.Main)
            .OrderByDescending(f => f.IsPrimary)
            .ThenByDescending(f => f.UploadedAt)
            .FirstOrDefault();
        if (main is null)
        {
            return;
        }

        _logger.LogInformation("Install requested from the detail page: mod {Mod} main file {File}", Mod.ModId, main.FileId);
        await _installs.InstallFromNexusAsync(Mod, main);
        RefreshState();
    }

    /// <summary>A requirement on Nexus opens as its own detail page; an off-site one opens in the browser.</summary>
    [RelayCommand]
    private async Task OpenRequirementAsync(BrowseRequirementViewModel? requirement)
    {
        if (requirement is null)
        {
            return;
        }

        if (requirement.Requirement.ModId is { } id)
        {
            await _openMod(id);
            return;
        }

        if (!string.IsNullOrWhiteSpace(requirement.Requirement.Url))
        {
            _windows.OpenUrl(requirement.Requirement.Url);
        }
    }

    /// <summary>The hero picture was clicked: show it large in the viewer.</summary>
    [RelayCommand]
    private void OpenHero()
    {
        var url = Mod.PictureUrl ?? Mod.ThumbnailUrl;
        if (!string.IsNullOrWhiteSpace(url))
        {
            _preview.Open(new[] { url }, 0, Name);
        }
    }

    public void Cancel() => _lifetime.Cancel();

    private static int Order(NexusFileCategory category) => category switch
    {
        NexusFileCategory.Main => 0,
        NexusFileCategory.Update => 1,
        NexusFileCategory.Optional => 2,
        NexusFileCategory.Miscellaneous => 3,
        NexusFileCategory.OldVersion => 4,
        _ => 5,
    };

    private static string Title(NexusFileCategory category) => category switch
    {
        NexusFileCategory.Main => "Main files",
        NexusFileCategory.Update => "Updates",
        NexusFileCategory.Optional => "Optional files",
        NexusFileCategory.Miscellaneous => "Miscellaneous",
        NexusFileCategory.OldVersion => "Old versions",
        _ => "Other files",
    };
}
