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

/// <summary>
/// Browse page: Trending / Latest added / Recently updated lists and text search on Nexus Mods as a grid of
/// cards; clicking a card opens the mod as a full page (Back returns to the grid exactly as it was). Works
/// without an API key (public GraphQL); downloads need one and start the connect dialog when it is missing.
/// Loads lazily the first time the page is shown.
/// </summary>
public sealed partial class BrowseViewModel : PageViewModel
{
    private const int PageSize = 24;

    private readonly INexusApiClient _client;
    private readonly INexusSession _session;
    private readonly IModService _mods;
    private readonly IModUpdateChecker _updates;
    private readonly IInstallCoordinator _installs;
    private readonly IWindowService _windows;
    private readonly IThumbnailCache _thumbnails;
    private readonly INexusConnectPrompt _connect;
    private readonly IToastService _toasts;
    private readonly ILogger<BrowseViewModel> _logger;

    /// <summary>Detail pages opened from a detail page (requirements); Back pops them before returning to the grid.</summary>
    private readonly Stack<BrowseDetailViewModel> _history = new();

    private int _loadVersion;
    private int _offset;
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
        _logger = logger;

        _session.Changed += (_, _) => OnUiThread(RefreshAccountState);
        _mods.Changed += (_, _) => OnUiThread(RefreshCardStates);
        _updates.Changed += (_, _) => OnUiThread(RefreshCardStates);
        _client.RateLimitChanged += (_, limit) => OnUiThread(() => RateLimitText = $"API budget: {limit.HourlyRemaining ?? 0} of {limit.HourlyLimit ?? 0} this hour");
        RefreshAccountState();
    }

    public override string Title => "Browse";

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
    [NotifyCanExecuteChangedFor(nameof(LoadMoreCommand), nameof(RefreshCommand))]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreCommand))]
    private bool _isLoadingMore;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreCommand))]
    private bool _hasMore;

    [ObservableProperty]
    private string? _errorText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty), nameof(TotalCountText))]
    private int _totalCount;

    public string TotalCountText => TotalCount == 1 ? "1 mod" : $"{TotalCount} mods";

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

        _ = LoadAsync(reset: true);
    }

    [RelayCommand]
    private Task SearchAsync()
    {
        var query = SearchText.Trim();
        _activeQuery = query;
        Tab = BrowseTab.Search;
        OnPropertyChanged(nameof(TabTitle));
        return LoadAsync(reset: true);
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task RefreshAsync() => LoadAsync(reset: true, refresh: true);

    private bool CanRefresh() => !IsLoading;

    [RelayCommand(CanExecute = nameof(CanLoadMore))]
    private Task LoadMoreAsync() => LoadAsync(reset: false);

    private bool CanLoadMore() => HasMore && !IsLoading && !IsLoadingMore;

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

        var detail = new BrowseDetailViewModel(mod, _client, _session, _mods, _updates, _installs, _windows, _thumbnails, ShowModAsync, _logger);
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
        _ = LoadAsync(reset: true);
    }

    private async Task LoadAsync(bool reset, bool refresh = false)
    {
        var version = ++_loadVersion;
        if (reset)
        {
            _offset = 0;
            IsLoading = true;
            ErrorText = null;
        }
        else
        {
            IsLoadingMore = true;
        }

        try
        {
            var page = Tab == BrowseTab.Search
                ? await _client.SearchAsync(_activeQuery, _offset, PageSize, refresh)
                : await _client.GetListAsync(ToKind(Tab), _offset, PageSize, refresh);

            if (version != _loadVersion)
            {
                return; // superseded by a newer request
            }

            if (reset)
            {
                Cards.Clear();
            }

            foreach (var mod in page.Items)
            {
                var card = new BrowseModCardViewModel(mod);
                card.RefreshState(_mods, _updates);
                card.IsSelected = Selected?.Mod.ModId == mod.ModId;
                Cards.Add(card);
                _ = LoadThumbnailAsync(card);
            }

            _offset += page.Items.Count;
            TotalCount = page.TotalCount;
            HasMore = page.HasMore && page.Items.Count > 0;
            _loadedOnce = true;
            _logger.LogDebug("Browse {Tab} loaded {Count} card(s), total {Total}, more: {More}", Tab, Cards.Count, TotalCount, HasMore);
        }
        catch (NexusException ex)
        {
            if (version != _loadVersion)
            {
                return;
            }

            _logger.LogWarning("Browse {Tab} failed: {Message}", Tab, ex.Message);
            ErrorText = ex.Message;
            _loadedOnce = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Browse {Tab} failed unexpectedly", Tab);
            ErrorText = "Something went wrong while talking to Nexus Mods. See the log for details.";
            _loadedOnce = true;
        }
        finally
        {
            if (version == _loadVersion)
            {
                IsLoading = false;
                IsLoadingMore = false;
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
