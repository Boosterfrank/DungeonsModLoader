using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DungeonsModLoader.Core.Mods;
using DungeonsModLoader.Nexus.Api;
using DungeonsModLoader.Nexus.Linking;
using Microsoft.Extensions.Logging;

namespace DungeonsModLoader.App.ViewModels.Dialogs;

/// <summary>
/// "Link mods to Nexus Mods": one row per local mod with the Nexus pages that may be it (best match preselected),
/// the installed version to record, and a search box for the cases the matcher misses. Shown as a dialog from the
/// Installed page and as a step of the first-run wizard. Searching uses the public API (no account needed).
/// </summary>
public sealed partial class LinkModsViewModel : ObservableObject, IDisposable
{
    /// <summary>Searches running at once (each mod needs a few requests; Nexus answers quickly but not for free).</summary>
    private const int SearchConcurrency = 3;

    private readonly IModLinkFinder _finder;
    private readonly INexusApiClient _client;
    private readonly IModService _mods;
    private readonly IThumbnailCache _thumbnails;
    private readonly ILogger<LinkModsViewModel> _logger;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _searchGate = new(SearchConcurrency, SearchConcurrency);
    private bool _disposed;

    public LinkModsViewModel(IModLinkFinder finder, INexusApiClient client, IModService mods, IThumbnailCache thumbnails, ILogger<LinkModsViewModel> logger)
    {
        _finder = finder;
        _client = client;
        _mods = mods;
        _thumbnails = thumbnails;
        _logger = logger;
    }

    /// <summary>Raised when the dialog should close; true when at least one mod was linked.</summary>
    public event EventHandler<bool>? CloseRequested;

    public ObservableCollection<LinkModRowViewModel> Rows { get; } = new();

    public bool HasRows => Rows.Count > 0;

    /// <summary>How many mods were linked by <see cref="ApplyAsync"/> so far.</summary>
    public int LinkedCount { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSearching), nameof(SummaryText))]
    private int _searchingCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(SummaryText), nameof(LinkButtonText))]
    [NotifyCanExecuteChangedFor(nameof(ApplyAndCloseCommand))]
    private int _selectedCount;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyAndCloseCommand), nameof(CancelCommand))]
    private bool _isApplying;

    [ObservableProperty]
    private string? _busyText;

    /// <summary>Summary of the rows that could not be linked; null when the last apply went through.</summary>
    [ObservableProperty]
    private string? _error;

    public bool IsSearching => SearchingCount > 0;

    public bool HasSelection => SelectedCount > 0;

    public string SummaryText
    {
        get
        {
            if (Rows.Count == 0)
            {
                return "No local mods to link.";
            }

            if (IsSearching)
            {
                return $"Searching Nexus Mods for {Rows.Count} {(Rows.Count == 1 ? "mod" : "mods")}...";
            }

            var matched = Rows.Count(r => r.HasCandidates || r.IsLinked);
            var linked = Rows.Count(r => r.IsLinked);
            if (linked == Rows.Count)
            {
                return "All mods are linked.";
            }

            return matched == 0
                ? "Nothing matched by name. Type the name of each mod as it appears on Nexus Mods and search, or leave them unlinked."
                : $"{matched} of {Rows.Count} {(Rows.Count == 1 ? "mod has" : "mods have")} a match. Check each pick (name and author), set the version you have, then link.";
        }
    }

    public string LinkButtonText => SelectedCount switch
    {
        0 => "Link",
        1 => "Link 1 mod",
        var n => $"Link {n} mods",
    };

    /// <summary>Fills the rows and starts the searches (a few at a time).</summary>
    public void Load(IEnumerable<ModInfo> mods)
    {
        ArgumentNullException.ThrowIfNull(mods);
        Rows.Clear();
        foreach (var mod in mods)
        {
            Rows.Add(new LinkModRowViewModel(mod, this));
        }

        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(SummaryText));
        RecountSelection();
        foreach (var row in Rows)
        {
            _ = SearchRowAsync(row, null);
        }
    }

    /// <summary>Searches for one row: by the mod's own names, or by <paramref name="query"/> when the user typed one.</summary>
    internal async Task SearchRowAsync(LinkModRowViewModel row, string? query)
    {
        var token = _lifetime.Token;
        row.IsSearching = true;
        SearchingCount++;
        try
        {
            await _searchGate.WaitAsync(token);
            ModLinkSuggestion suggestion;
            try
            {
                suggestion = string.IsNullOrWhiteSpace(query)
                    ? await _finder.FindAsync(row.Mod, token)
                    : await _finder.SearchAsync(row.Mod, query, token);
            }
            finally
            {
                _searchGate.Release();
            }

            if (token.IsCancellationRequested)
            {
                return;
            }

            row.SetCandidates(suggestion);
            foreach (var candidate in row.Candidates)
            {
                if (!candidate.IsNone)
                {
                    _ = LoadThumbnailAsync(candidate, token);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Searching Nexus Mods for '{Mod}' failed", row.Name);
            row.Error = "Nexus Mods could not be searched right now. Try again in a moment.";
        }
        finally
        {
            row.IsSearching = false;
            SearchingCount = Math.Max(0, SearchingCount - 1);
            RecountSelection();
            OnPropertyChanged(nameof(SummaryText));
        }
    }

    /// <summary>Lists the main files of the chosen page so the user can say which version is installed (newest first).</summary>
    internal async Task LoadFilesAsync(LinkModRowViewModel row, LinkCandidateViewModel candidate)
    {
        var token = _lifetime.Token;
        var mod = candidate.Mod;
        if (mod is null)
        {
            return;
        }

        row.IsLoadingFiles = true;
        row.Files.Clear();
        try
        {
            var list = await _client.GetFilesAsync(mod.ModId, cancellationToken: token);
            if (token.IsCancellationRequested || !ReferenceEquals(row.Selected, candidate))
            {
                return;
            }

            var mains = list.Files
                .Where(f => f.Category == NexusFileCategory.Main && f.IsDownloadable)
                .OrderByDescending(f => f.UploadedAt)
                .ThenByDescending(f => f.FileId)
                .ToList();
            for (var i = 0; i < mains.Count; i++)
            {
                row.Files.Add(new LinkFileOptionViewModel(mains[i], isNewest: i == 0));
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogDebug("File list of Nexus mod {Mod} could not be loaded for linking: {Message}", mod.ModId, ex.Message);
        }
        finally
        {
            row.IsLoadingFiles = false;
        }

        if (ReferenceEquals(row.Selected, candidate))
        {
            row.Files.Add(LinkFileOptionViewModel.Unknown());
            row.SelectedFile = row.Files[0];
        }
    }

    private async Task LoadThumbnailAsync(LinkCandidateViewModel candidate, CancellationToken token)
    {
        try
        {
            candidate.ThumbnailPath = await _thumbnails.GetFileAsync(candidate.Mod?.ThumbnailUrl ?? candidate.Mod?.PictureUrl, token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Thumbnail of Nexus mod {Mod} could not be loaded", candidate.Mod?.ModId);
        }
    }

    internal void RecountSelection()
    {
        SelectedCount = Rows.Count(r => r.HasRealSelection && !r.IsLinked);
    }

    /// <summary>
    /// Writes the chosen links into the manifest. Rows that fail keep an error note and stay selectable; returns
    /// how many mods were linked by this call.
    /// </summary>
    public async Task<int> ApplyAsync()
    {
        if (IsApplying)
        {
            return 0;
        }

        var rows = Rows.Where(r => r.HasRealSelection && !r.IsLinked).ToList();
        if (rows.Count == 0)
        {
            return 0;
        }

        IsApplying = true;
        Error = null;
        var linked = 0;
        var failures = 0;
        try
        {
            foreach (var row in rows)
            {
                var mod = row.Selected?.Mod;
                if (mod is null)
                {
                    continue;
                }

                var file = row.SelectedFile?.File;
                BusyText = $"Linking {row.Name}...";
                try
                {
                    var requirements = await TryGetRequirementsAsync(mod.ModId);
                    var version = file is null ? null : string.IsNullOrWhiteSpace(file.Version) ? mod.Version : file.Version;
                    var link = new NexusLink(
                        mod.ModId,
                        file?.FileId,
                        version,
                        mod.Author ?? mod.Uploader,
                        mod.ThumbnailUrl ?? mod.PictureUrl,
                        requirements,
                        file?.UploadedAt);
                    await _mods.LinkToNexusAsync(row.Mod.Entry.Id, link, _lifetime.Token);
                    row.Error = null;
                    row.IsLinked = true;
                    linked++;
                    _logger.LogInformation("Linked '{Mod}' to Nexus mod {NexusId} '{Name}' (file {File})", row.Name, mod.ModId, mod.Name, file?.FileId.ToString() ?? "unknown");
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Linking '{Mod}' to Nexus mod {NexusId} failed", row.Name, mod.ModId);
                    row.Error = ex is ModNotFoundException or ModOperationException ? ex.Message : "The link could not be saved. See the log for details.";
                    failures++;
                }
            }
        }
        finally
        {
            IsApplying = false;
            BusyText = null;
        }

        LinkedCount += linked;
        Error = failures switch
        {
            0 => null,
            1 => "One mod could not be linked; see the note under it.",
            _ => $"{failures} mods could not be linked; see the notes under them.",
        };
        RecountSelection();
        OnPropertyChanged(nameof(SummaryText));
        return linked;
    }

    private bool CanApply() => !IsApplying && HasSelection;

    /// <summary>The dialog's Link button: applies and closes when everything went through.</summary>
    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAndCloseAsync()
    {
        await ApplyAsync();
        if (Error is null)
        {
            CloseRequested?.Invoke(this, LinkedCount > 0);
        }
    }

    private bool CanCancel() => !IsApplying;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => CloseRequested?.Invoke(this, LinkedCount > 0);

    private async Task<IReadOnlyList<ModRequirementRecord>?> TryGetRequirementsAsync(long modId)
    {
        try
        {
            var requirements = await _client.GetRequirementsAsync(modId, cancellationToken: _lifetime.Token);
            return requirements.Select(r => new ModRequirementRecord(r.ModId, r.Name, r.Url, r.Notes)).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug("Requirements of mod {Mod} could not be loaded: {Message}", modId, ex.Message);
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}

/// <summary>One local mod to link: its names, the candidate pages, the version picker and a manual search.</summary>
public sealed partial class LinkModRowViewModel : ObservableObject
{
    private readonly LinkModsViewModel _owner;

    public LinkModRowViewModel(ModInfo mod, LinkModsViewModel owner)
    {
        Mod = mod;
        _owner = owner;
        Detail = BuildDetail(mod.Entry);
        Candidates.Add(LinkCandidateViewModel.None());
        _selected = Candidates[0];
    }

    public ModInfo Mod { get; }

    public string Name => string.IsNullOrWhiteSpace(Mod.Entry.DisplayName) ? Mod.Entry.FolderName : Mod.Entry.DisplayName;

    /// <summary>Folder name and pak files: what identifies the mod when the display name does not.</summary>
    public string Detail { get; }

    /// <summary>"Don't link" first, then the Nexus pages found, best first.</summary>
    public ObservableCollection<LinkCandidateViewModel> Candidates { get; } = new();

    /// <summary>Main files of the selected page, newest first, then "not sure".</summary>
    public ObservableCollection<LinkFileOptionViewModel> Files { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private bool _isSearching;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRealSelection), nameof(StatusText), nameof(ShowFiles))]
    private LinkCandidateViewModel? _selected;

    [ObservableProperty]
    private LinkFileOptionViewModel? _selectedFile;

    [ObservableProperty]
    private bool _isLoadingFiles;

    /// <summary>Text of the manual search box.</summary>
    [ObservableProperty]
    private string _query = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(HasError))]
    private string? _error;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(ShowPicker), nameof(ShowFiles))]
    private bool _isLinked;

    public bool HasRealSelection => Selected is { IsNone: false };

    public bool HasCandidates => Candidates.Count > 1;

    public bool HasError => Error is not null;

    public bool ShowPicker => !IsLinked;

    public bool ShowFiles => HasRealSelection && !IsLinked;

    public string StatusText
    {
        get
        {
            if (IsLinked)
            {
                return "Linked to its Nexus Mods page.";
            }

            if (IsSearching)
            {
                return "Searching Nexus Mods...";
            }

            if (Error is not null)
            {
                return Error;
            }

            if (!HasCandidates)
            {
                return "No match found. Type the name as it appears on Nexus Mods and search, or leave the mod unlinked.";
            }

            return Selected is { IsNone: false } selected
                ? selected.ConfidenceText
                : "Pick the page this mod came from, or leave it unlinked.";
        }
    }

    partial void OnSelectedChanged(LinkCandidateViewModel? value)
    {
        Files.Clear();
        SelectedFile = null;
        if (value is { IsNone: false })
        {
            _ = _owner.LoadFilesAsync(this, value);
        }

        _owner.RecountSelection();
    }

    [RelayCommand]
    private Task SearchAsync() => _owner.SearchRowAsync(this, Query);

    /// <summary>Replaces the candidates with a search result; the best non-weak match is preselected.</summary>
    public void SetCandidates(ModLinkSuggestion suggestion)
    {
        ArgumentNullException.ThrowIfNull(suggestion);
        var best = suggestion.Best;
        LinkCandidateViewModel? preselect = null;
        Candidates.Clear();
        Candidates.Add(LinkCandidateViewModel.None());
        foreach (var candidate in suggestion.Candidates)
        {
            var item = new LinkCandidateViewModel(candidate);
            Candidates.Add(item);
            if (ReferenceEquals(candidate, best))
            {
                preselect = item;
            }
        }

        Error = suggestion.Error;
        Selected = preselect ?? Candidates[0];
        OnPropertyChanged(nameof(HasCandidates));
        OnPropertyChanged(nameof(StatusText));
    }

    private static string BuildDetail(ModEntry entry)
    {
        var paks = entry.Files
            .Where(f => f.RelativePath.EndsWith(".pak", StringComparison.OrdinalIgnoreCase))
            .Select(f => Path.GetFileName(f.RelativePath))
            .Take(3)
            .ToList();
        return paks.Count == 0 ? entry.FolderName : $"{entry.FolderName} · {string.Join(", ", paks)}";
    }
}

/// <summary>A Nexus page that may be the local mod, or the "keep it local" choice.</summary>
public sealed partial class LinkCandidateViewModel : ObservableObject
{
    public LinkCandidateViewModel(ModLinkCandidate? candidate)
    {
        Candidate = candidate;
    }

    public static LinkCandidateViewModel None() => new(null);

    public ModLinkCandidate? Candidate { get; }

    public NexusMod? Mod => Candidate?.Mod;

    public bool IsNone => Candidate is null;

    public bool HasSubtitle => !IsNone;

    public string Title => IsNone ? "Don't link (keep as a local mod)" : Mod!.Name;

    public string Subtitle => IsNone
        ? string.Empty
        : $"by {Mod!.Author ?? Mod.Uploader ?? "unknown"} · {Format.Count(Mod.Downloads)} downloads · {Candidate!.ScorePercent}% match";

    public string ConfidenceText => Candidate?.Confidence switch
    {
        ModLinkConfidence.High => "Same name as the page on Nexus Mods.",
        ModLinkConfidence.Medium => "Likely the right page; check the name and author before linking.",
        ModLinkConfidence.Low => "Weak match; check carefully before linking.",
        _ => string.Empty,
    };

    /// <summary>Cached picture once downloaded; null shows the placeholder.</summary>
    [ObservableProperty]
    private string? _thumbnailPath;
}

/// <summary>One main file of the chosen page (the version the user has), or "not sure".</summary>
public sealed class LinkFileOptionViewModel
{
    public LinkFileOptionViewModel(NexusFile? file, bool isNewest)
    {
        File = file;
        IsNewest = isNewest;
    }

    public static LinkFileOptionViewModel Unknown() => new(null, false);

    public NexusFile? File { get; }

    public bool IsNewest { get; }

    public bool IsUnknown => File is null;

    public string Title
    {
        get
        {
            if (File is null)
            {
                return "Not sure which version is installed (the newest file is then offered as an update)";
            }

            var version = string.IsNullOrWhiteSpace(File.Version) ? File.Name : $"v{File.Version} · {File.Name}";
            return $"{version} · {Format.Relative(File.UploadedAt)}{(IsNewest ? " · newest" : string.Empty)}";
        }
    }
}
