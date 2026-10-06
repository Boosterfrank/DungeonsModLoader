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

/// <summary>A file of the mod in the detail panel, with its Install / Update / Installed button.</summary>
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

/// <summary>
/// The detail panel of the Browse page: description (BBCode), requirements and the file list with Install
/// buttons. Loads its three parts independently so one failing request does not blank the others.
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
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _lifetime = new();

    public BrowseDetailViewModel(
        NexusMod mod,
        INexusApiClient client,
        INexusSession session,
        IModService mods,
        IModUpdateChecker updates,
        IInstallCoordinator installs,
        IWindowService windows,
        IThumbnailCache thumbnails,
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
    private string? _picturePath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRequirements))]
    private int _requirementCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstalledText), nameof(ShowInstalled))]
    private bool _isInstalled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstalledText))]
    private string? _installedVersion;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstalledText), nameof(ShowInstalled))]
    private bool _hasUpdate;

    [ObservableProperty]
    private bool _hasFiles;

    /// <summary>What the download will do when the user has no key / is a free member (shown above the files).</summary>
    [ObservableProperty]
    private string? _downloadHint;

    public bool HasRequirements => RequirementCount > 0;

    public bool ShowInstalled => IsInstalled;

    public string InstalledText => !IsInstalled
        ? string.Empty
        : HasUpdate
            ? $"Installed ({InstalledVersion ?? "unknown version"}) · update available"
            : $"Installed ({InstalledVersion ?? "unknown version"})";

    /// <summary>Loads description, files and requirements; call once after construction.</summary>
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

            HasFiles = FileGroups.Count > 0;
            FilesError = HasFiles ? null : "This mod has no downloadable files right now.";
            OnPropertyChanged(nameof(HasMainFile));
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
        InstalledVersion = installed?.Entry.Version;
        var update = installed is not null && _updates.Updates.TryGetValue(installed.Entry.Id, out var pending) ? pending : null;
        HasUpdate = update is not null;

        foreach (var file in FileGroups.SelectMany(g => g.Files))
        {
            file.IsInstalledFile = installed?.Entry.NexusFileId == file.File.FileId;
            file.IsUpdateTarget = update?.NewFile.FileId == file.File.FileId;
        }

        foreach (var requirement in Requirements)
        {
            requirement.RefreshState(_mods);
        }

        DownloadHint = !_session.HasApiKey
            ? "Downloads need your Nexus Mods API key. Add it on the Settings page; browsing works without it."
            : !_session.IsPremium && !Mod.DirectDownloadEnabled
                ? "Free account: Install opens the file's page on Nexus Mods, where \"Mod Manager Download\" sends the file to this app."
                : null;
    }

    [RelayCommand]
    private void OpenOnNexus() => _windows.OpenUrl(Mod.PageUrl);

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

    /// <summary>Installs the primary (or newest main) file: the one-click action at the top of the panel.</summary>
    [RelayCommand]
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

        await _installs.InstallFromNexusAsync(Mod, main);
        RefreshState();
    }

    public bool HasMainFile => FileGroups.SelectMany(g => g.Files).Any(f => f.File.Category == NexusFileCategory.Main);

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
