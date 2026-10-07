using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DungeonsModLoader.App.Services;
using DungeonsModLoader.Core.Mods;
using DungeonsModLoader.Nexus;
using DungeonsModLoader.Nexus.Api;
using DungeonsModLoader.Nexus.Auth;
using DungeonsModLoader.Nexus.Updates;
using Microsoft.Extensions.Logging;

namespace DungeonsModLoader.App.ViewModels.Pages;

public enum BrowseTab
{
    Trending,
    LatestAdded,
    RecentlyUpdated,
    Search,
}

/// <summary>One entry of the pager under the grid: a page number, or a "…" gap between distant numbers.</summary>
public sealed record PageLinkViewModel(int Number, bool IsCurrent, bool IsGap)
{
    public string Text => IsGap ? "…" : Number.ToString();

    public bool IsClickable => !IsGap;

    public string AutomationName => IsGap ? "More pages" : $"Page {Number}";
}

/// <summary>
/// Browse page: Trending / Latest added / Recently updated lists and text search on Nexus Mods as a grid of
/// cards with page navigation (24 per page; Nexus lists are long); clicking a card opens the mod as a full page
/// (Back returns to the same page of the grid). Works without an API key (public GraphQL); downloads need one
/// and start the connect dialog when it is missing. Loads lazily the first time the page is shown.
/// </summary>
public sealed partial class BrowseViewModel : PageViewModel
{
    public const int PageSize = 24;

    /// <summary>Page numbers shown at once; beyond that the pager shows the first, the last and a window around the current page.</summary>
    private const int PagerSlots = 7;

    private readonly INexusApiClient _client;
    private readonly INexusSession _session;
    private readonly IModService _mods;
    private readonly IModUpdateChecker _updates;
    private readonly IInstallCoordinator _installs;
    private readonly IWindowService _windows;
    private readonly IThumbnailCache _thumbnails;
    private readonly INexusConnectPrompt _connect;
    private readonly IToastService _toasts;
    private readonly ImagePreviewViewModel _preview;
    private readonly ILogger<BrowseViewModel> _logger;

    /// <summary>Detail pages opened from a detail page (requirements); Back pops them before returning to the grid.</summary>
    private readonly Stack<BrowseDetailViewModel> _history = new();

    private int _loadVersion;
    private string _activeQuery = string.Empty;
    private bool _loadedOnce;

    public BrowseViewModel(
        INexusApiClient client,
        INexusSession session,
        IModService mods,
        IModUpdateChecker updates,
        IInstallCoordinator installs,
        IWindowService windows,
        IThumbnailCache thumbnails,
        INexusConnectPrompt connect,
        IToastService toasts,
        ImagePreviewViewModel preview,
        ILogger<BrowseViewModel> logger)
    {
        _client = client;
        _session = session;
        _mods = mods;
        _updates = updates;
        _installs = installs;
        _windows = windows;
        _thumbnails = thumbnails;
        _connect = connect;
        _toasts = toasts;
        _preview = preview;
        _logger = logger;

        _session.Changed += (_, _) => OnUiThread(RefreshAccountState);
        _mods.Changed += (_, _) => OnUiThread(RefreshCardStates);
        _updates.Changed += (_, _) => OnUiThread(RefreshCardStates);
        _client.RateLimitChanged += (_, limit) => OnUiThread(() => RateLimitText = $"API budget: {limit.HourlyRemaining ?? 0} of {limit.HourlyLimit ?? 0} this hour");
        RefreshAccountState();
    }

    public override string Title => "Browse";

    /// <summary>Raised after a different page of results was put into <see cref="Cards"/> (the view scrolls back to the top).</summary>
    public event EventHandler? PageChanged;

    public ObservableCollection<BrowseModCardViewModel> Cards { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTrendingTab), nameof(IsLatestTab), nameof(IsUpdatedTab), nameof(IsSearchTab), nameof(TabTitle))]
    private BrowseTab _tab = BrowseTab.Trending;

    public bool IsTrendingTab
    {
        get => Tab == BrowseTab.Trending;
        set => SelectTab(value, BrowseTab.Trending);
    }

    public bool IsLatestTab
    {
        get => Tab == BrowseTab.LatestAdded;
        set => SelectTab(value, BrowseTab.LatestAdded);
    }

    public bool IsUpdatedTab
    {
        get => Tab == BrowseTab.RecentlyUpdated;
        set => SelectTab(value, BrowseTab.RecentlyUpdated);
    }

    public bool IsSearchTab => Tab == BrowseTab.Search;

    public string TabTitle => Tab switch
    {
        BrowseTab.Trending => "Trending",
        BrowseTab.LatestAdded => "Latest added",
        BrowseTab.RecentlyUpdated => "Recently updated",
        _ => _activeQuery.Length == 0 ? "All mods" : $"Results for \"{_activeQuery}\"",
    };

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand), nameof(NextPageCommand), nameof(PreviousPageCommand), nameof(GoToPageCommand))]
    private bool _isLoading;

    [ObservableProperty]
    private string? _errorText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty), nameof(TotalCountText))]
    private int _totalCount;

    public string TotalCountText => TotalCount == 1 ? "1 mod" : $"{TotalCount} mods";

    // ------------------------------------------------------------------------------------------------------
    // Pages
    // ------------------------------------------------------------------------------------------------------

    /// <summary>1-based page of results in the grid.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PagerText))]
    [NotifyCanExecuteChangedFor(nameof(NextPageCommand), nameof(PreviousPageCommand))]
    private int _pageIndex = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PagerText), nameof(ShowPager))]
    [NotifyCanExecuteChangedFor(nameof(NextPageCommand), nameof(PreviousPageCommand))]
    private int _pageCount = 1;

    /// <summary>The numbers (and gaps) of the pager, rebuilt with every page load.</summary>
    public ObservableCollection<PageLinkViewModel> PageLinks { get; } = new();

    public string PagerText => PageCount <= 1 ? string.Empty : $"Page {PageIndex} of {PageCount}";

    public bool ShowPager => PageCount > 1;

    private bool CanGoNext() => !IsLoading && PageIndex < PageCount;

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private Task NextPageAsync() => LoadPageAsync(PageIndex + 1);

    private bool CanGoPrevious() => !IsLoading && PageIndex > 1;

    [RelayCommand(CanExecute = nameof(CanGoPrevious))]
    private Task PreviousPageAsync() => LoadPageAsync(PageIndex - 1);

    private bool CanGoToPage(PageLinkViewModel? link) => !IsLoading && link is { IsGap: false };

    [RelayCommand(CanExecute = nameof(CanGoToPage))]
    private Task GoToPageAsync(PageLinkViewModel? link) =>
        link is null || link.IsGap || link.Number == PageIndex ? Task.CompletedTask : LoadPageAsync(link.Number);

    /// <summary>First, last and a window around the current page; "…" where numbers are skipped.</summary>
    private void RebuildPageLinks()
    {
        PageLinks.Clear();
        if (PageCount <= 1)
        {
            return;
        }

        var numbers = new List<int>();
        if (PageCount <= PagerSlots)
        {
            numbers.AddRange(Enumerable.Range(1, PageCount));
        }
        else
        {
            var inner = PagerSlots - 2; // slots left after the first and last page
            var start = Math.Clamp(PageIndex - inner / 2, 2, PageCount - inner);
            var end = start + inner - 1;
            numbers.Add(1);
            if (start > 2)
            {
                numbers.Add(0); // gap
                start++;
            }

            if (end < PageCount - 1)
            {
                end--;
            }

            numbers.AddRange(Enumerable.Range(start, end - start + 1));
            if (end < PageCount - 1)
            {
                numbers.Add(0);
            }

            numbers.Add(PageCount);
        }

        foreach (var number in numbers)
        {
            PageLinks.Add(number == 0 ? new PageLinkViewModel(0, false, true) : new PageLinkViewModel(number, number == PageIndex, false));
        }
    }

    // ------------------------------------------------------------------------------------------------------
    // Account, detail
    // ------------------------------------------------------------------------------------------------------

    [ObservableProperty]
    private string _accountText = string.Empty;

    [ObservableProperty]
    private string? _rateLimitText;

    [ObservableProperty]
    private bool _showLoginHint;

    /// <summary>The mod shown as a full page; null shows the grid.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(ShowList))]
    private BrowseDetailViewModel? _selected;

    public bool HasSelection => Selected is not null;

    public bool ShowList => Selected is null;

    /// <summary>True while a mod that is not in the grid is fetched for its detail page (a requirement, a hint on the Installed page).</summary>
    [ObservableProperty]
    private bool _isOpeningMod;

    public bool ShowEmpty => !IsLoading && Cards.Count == 0 && ErrorText is null && _loadedOnce;

    /// <summary>Called when the page becomes visible: loads the first list once.</summary>
    public void EnsureLoaded()
    {
        if (_loadedOnce || IsLoading)
        {
            return;
        }

        _ = LoadPageAsync(1);
    }

    [RelayCommand]
    private Task SearchAsync()
    {
        var query = SearchText.Trim();
        _activeQuery = query;
        Tab = BrowseTab.Search;
        OnPropertyChanged(nameof(TabTitle));
        return LoadPageAsync(1);
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task RefreshAsync() => LoadPageAsync(PageIndex, refresh: true);

    private bool CanRefresh() => !IsLoading;

    /// <summary>A card was clicked: open the mod as a full page.</summary>
    [RelayCommand]
    private Task SelectAsync(BrowseModCardViewModel? card) => card is null ? Task.CompletedTask : OpenDetailAsync(card.Mod);

    /// <summary>
    /// Opens the detail page of a mod by Nexus id: from the grid when it is there, otherwise fetched from Nexus
    /// (requirement links, dependency hints on the Installed page). Problems are reported as a toast.
    /// </summary>
    public async Task ShowModAsync(long modId)
    {
        if (Selected?.Mod.ModId == modId)
        {
            return;
        }

        var mod = Cards.FirstOrDefault(c => c.ModId == modId)?.Mod ?? _history.FirstOrDefault(d => d.Mod.ModId == modId)?.Mod;
        if (mod is null)
        {
            IsOpeningMod = true;
            try
            {
                mod = await _client.GetModAsync(modId);
            }
            catch (NexusException ex)
            {
                _logger.LogWarning("Mod {Mod} could not be opened: {Message}", modId, ex.Message);
                _toasts.Show(ex.Message, ToastKind.Warning, "Could not open the mod");
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Mod {Mod} could not be opened", modId);
                _toasts.Show("Something went wrong while talking to Nexus Mods. See the log for details.", ToastKind.Error, "Could not open the mod");
                return;
            }
            finally
            {
                IsOpeningMod = false;
            }
        }

        await OpenDetailAsync(mod);
    }

    private async Task OpenDetailAsync(NexusMod mod)
    {
        if (Selected is { } current)
        {
            if (current.Mod.ModId == mod.ModId)
            {
                return;
            }

            _history.Push(current);
        }

        var detail = new BrowseDetailViewModel(mod, _client, _session, _mods, _updates, _installs, _windows, _thumbnails, _preview, ShowModAsync, _logger);
        Selected = detail;
        HighlightCard(mod.ModId);
        _logger.LogDebug("Opened the detail page of mod {Mod} ({Name}); {Depth} page(s) behind it", mod.ModId, mod.Name, _history.Count);

        try
        {
            await detail.LoadAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Loading the detail page of mod {Mod} failed", mod.ModId);
        }
    }

    /// <summary>The back arrow (also Esc): the previous detail page when there is one, otherwise the grid.</summary>
    [RelayCommand]
    private void Back()
    {
        if (Selected is null)
        {
            return;
        }

        Selected.Cancel();
        if (_history.Count > 0)
        {
            var previous = _history.Pop();
            previous.RefreshState();
            Selected = previous;
            HighlightCard(previous.Mod.ModId);
        }
        else
        {
            Selected = null;
            HighlightCard(null);
        }
    }

    [RelayCommand]
    private Task ConnectNexusAsync() => _connect.ShowAsync("Connect your account to download mods and to get update notices.");

    [RelayCommand]
    private void OpenNexusGamePage() => _windows.OpenUrl($"{NexusConstants.WebsiteBaseUrl}/games/{NexusConstants.GameDomain}");

    private void SelectTab(bool selected, BrowseTab tab)
    {
        if (!selected || Tab == tab)
        {
            return;
        }

        Tab = tab;
        _ = LoadPageAsync(1);
    }

    // ------------------------------------------------------------------------------------------------------
    // Loading
    // ------------------------------------------------------------------------------------------------------

    /// <summary>Loads one page of the current list / search into the grid (replacing what is there).</summary>
    private async Task LoadPageAsync(int page, bool refresh = false)
    {
        page = Math.Max(1, page);
        var version = ++_loadVersion;
        var offset = (page - 1) * PageSize;
        IsLoading = true;
        ErrorText = null;

        try
        {
            var result = Tab == BrowseTab.Search
                ? await _client.SearchAsync(_activeQuery, offset, PageSize, refresh)
                : await _client.GetListAsync(ToKind(Tab), offset, PageSize, refresh);

            if (version != _loadVersion)
            {
                return; // superseded by a newer request
            }

            Cards.Clear();
            foreach (var mod in result.Items)
            {
                var card = new BrowseModCardViewModel(mod);
                card.RefreshState(_mods, _updates);
                card.IsSelected = Selected?.Mod.ModId == mod.ModId;
                Cards.Add(card);
                _ = LoadThumbnailAsync(card);
            }

            TotalCount = result.TotalCount;
            PageCount = Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));
            PageIndex = Math.Min(page, PageCount);
            RebuildPageLinks();
            _loadedOnce = true;
            _logger.LogDebug("Browse {Tab} page {Page}/{Pages}: {Count} card(s), total {Total}", Tab, PageIndex, PageCount, Cards.Count, TotalCount);
            PageChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (NexusException ex)
        {
            if (version != _loadVersion)
            {
                return;
            }

            _logger.LogWarning("Browse {Tab} page {Page} failed: {Message}", Tab, page, ex.Message);
            ErrorText = ex.Message;
            _loadedOnce = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Browse {Tab} page {Page} failed unexpectedly", Tab, page);
            ErrorText = "Something went wrong while talking to Nexus Mods. See the log for details.";
            _loadedOnce = true;
        }
        finally
        {
            if (version == _loadVersion)
            {
                IsLoading = false;
                OnPropertyChanged(nameof(ShowEmpty));
            }
        }
    }

    private async Task LoadThumbnailAsync(BrowseModCardViewModel card)
    {
        try
        {
            var path = await _thumbnails.GetFileAsync(card.ThumbnailUrl);
            if (path is not null)
            {
                OnUiThread(() => card.ThumbnailPath = path);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Thumbnail for mod {Mod} failed", card.ModId);
        }
    }

    private void HighlightCard(long? modId)
    {
        foreach (var card in Cards)
        {
            card.IsSelected = card.ModId == modId;
        }
    }

    private void RefreshCardStates()
    {
        foreach (var card in Cards)
        {
            card.RefreshState(_mods, _updates);
        }

        Selected?.RefreshState();
    }

    private void RefreshAccountState()
    {
        AccountText = _session.Status switch
        {
            NexusSessionStatus.LoggedIn => $"Logged in as {_session.User?.Name} ({(_session.IsPremium ? "Premium" : "Free account")})",
            NexusSessionStatus.Unverified => "Nexus Mods account: key stored, not verified yet",
            NexusSessionStatus.Verifying => "Checking your Nexus Mods account...",
            _ => "Not logged in to Nexus Mods",
        };
        ShowLoginHint = !_session.HasApiKey;
        Selected?.RefreshState();
    }

    private static NexusListKind ToKind(BrowseTab tab) => tab switch
    {
        BrowseTab.LatestAdded => NexusListKind.LatestAdded,
        BrowseTab.RecentlyUpdated => NexusListKind.RecentlyUpdated,
        _ => NexusListKind.Trending,
    };

    private static void OnUiThread(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action);
        }
    }
}
