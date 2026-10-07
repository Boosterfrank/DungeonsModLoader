using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DungeonsModLoader.App.Services;
using DungeonsModLoader.Nexus.Api;
using Microsoft.Extensions.Logging;

namespace DungeonsModLoader.App.ViewModels;

/// <summary>
/// The full-window picture viewer (lightbox) over the shell: a list of picture URLs, the current one shown large,
/// previous / next, Esc or a click on the backdrop closes it. One instance for the app (DI singleton); the Browse
/// detail page opens it.
/// </summary>
public sealed partial class ImagePreviewViewModel : ObservableObject
{
    private readonly IThumbnailCache _cache;
    private readonly IWindowService _windows;
    private readonly ILogger<ImagePreviewViewModel> _logger;
    private IReadOnlyList<string> _urls = Array.Empty<string>();
    private int _loadVersion;

    public ImagePreviewViewModel(IThumbnailCache cache, IWindowService windows, ILogger<ImagePreviewViewModel> logger)
    {
        _cache = cache;
        _windows = windows;
        _logger = logger;
    }

    [ObservableProperty]
    private bool _isOpen;

    /// <summary>Name of the mod the pictures belong to (shown in the viewer's header).</summary>
    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CounterText), nameof(CurrentUrl))]
    private int _index;

    /// <summary>Cached file of the current picture; null while loading or when it failed.</summary>
    [ObservableProperty]
    private string? _currentPath;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _errorText;

    public int Count => _urls.Count;

    public bool HasMany => Count > 1;

    public string CounterText => Count == 0 ? string.Empty : $"{Index + 1} / {Count}";

    public string? CurrentUrl => Index >= 0 && Index < Count ? _urls[Index] : null;

    /// <summary>Shows <paramref name="urls"/> starting at <paramref name="index"/>.</summary>
    public void Open(IReadOnlyList<string> urls, int index, string title)
    {
        ArgumentNullException.ThrowIfNull(urls);
        if (urls.Count == 0)
        {
            return;
        }

        _urls = urls;
        Title = title;
        Index = Math.Clamp(index, 0, urls.Count - 1);
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(HasMany));
        NextCommand.NotifyCanExecuteChanged();
        PreviousCommand.NotifyCanExecuteChanged();
        IsOpen = true;
        _logger.LogDebug("Picture viewer opened: {Count} picture(s) of {Title}, starting at {Index}", urls.Count, title, Index);
        _ = LoadCurrentAsync();
    }

    [RelayCommand]
    private void Close()
    {
        if (!IsOpen)
        {
            return;
        }

        _loadVersion++;
        IsOpen = false;
        CurrentPath = null;
        IsLoading = false;
        ErrorText = null;
    }

    private bool CanStep() => IsOpen && HasMany;

    [RelayCommand(CanExecute = nameof(CanStep))]
    private void Next()
    {
        Index = (Index + 1) % Count;
        _ = LoadCurrentAsync();
    }

    [RelayCommand(CanExecute = nameof(CanStep))]
    private void Previous()
    {
        Index = (Index - 1 + Count) % Count;
        _ = LoadCurrentAsync();
    }

    [RelayCommand]
    private void OpenInBrowser()
    {
        if (CurrentUrl is { } url)
        {
            _windows.OpenUrl(url);
        }
    }

    private async Task LoadCurrentAsync()
    {
        var version = ++_loadVersion;
        var url = CurrentUrl;
        CurrentPath = null;
        ErrorText = null;
        IsLoading = true;
        try
        {
            var path = url is null ? null : await _cache.GetFileAsync(url);
            if (version != _loadVersion)
            {
                return;
            }

            if (path is null)
            {
                ErrorText = "This picture could not be loaded. You can open it in your browser instead.";
            }
            else
            {
                CurrentPath = path;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (version == _loadVersion)
            {
                _logger.LogDebug(ex, "Picture {Url} could not be loaded", url);
                ErrorText = "This picture could not be loaded. You can open it in your browser instead.";
            }
        }
        finally
        {
            if (version == _loadVersion)
            {
                IsLoading = false;
            }
        }
    }
}
