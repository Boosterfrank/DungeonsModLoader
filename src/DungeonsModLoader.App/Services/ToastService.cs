using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;

namespace DungeonsModLoader.App.Services;

/// <inheritdoc cref="IToastService"/>
public sealed class ToastService : IToastService
{
    /// <summary>More than this and the oldest non-sticky toast makes room.</summary>
    private const int MaxVisible = 4;

    private static readonly TimeSpan DefaultDuration = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan WarningDuration = TimeSpan.FromSeconds(7);
    private static readonly TimeSpan ErrorDuration = TimeSpan.FromSeconds(9);

    private readonly ObservableCollection<ToastViewModel> _toasts = new();
    private readonly ILogger<ToastService> _logger;

    public ToastService(ILogger<ToastService> logger)
    {
        _logger = logger;
        Toasts = new ReadOnlyObservableCollection<ToastViewModel>(_toasts);
    }

    public ReadOnlyObservableCollection<ToastViewModel> Toasts { get; }

    public ToastViewModel Show(string message, ToastKind kind = ToastKind.Info, string? title = null, TimeSpan? duration = null, string? actionText = null, Action? action = null, bool keepOpenOnAction = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        var lifetime = duration ?? DefaultFor(kind);
        var sticky = lifetime == Timeout.InfiniteTimeSpan || lifetime <= TimeSpan.Zero;
        var toast = new ToastViewModel(this, message, kind, title, actionText, action, sticky, keepOpenOnAction);
        _logger.LogDebug("Toast ({Kind}{Sticky}): {Message}", kind, sticky ? ", sticky" : string.Empty, message);

        OnUiThread(() =>
        {
            while (_toasts.Count >= MaxVisible)
            {
                var victim = _toasts.FirstOrDefault(t => !t.IsSticky) ?? _toasts[0];
                Remove(victim);
            }

            _toasts.Add(toast);
            if (!sticky)
            {
                _ = DismissLaterAsync(toast, lifetime);
            }
        });

        return toast;
    }

    public void Dismiss(ToastViewModel toast)
    {
        ArgumentNullException.ThrowIfNull(toast);
        OnUiThread(() => Remove(toast));
    }

    private async Task DismissLaterAsync(ToastViewModel toast, TimeSpan lifetime)
    {
        await Task.Delay(lifetime);
        Remove(toast);
    }

    private void Remove(ToastViewModel toast)
    {
        if (toast.IsDismissed)
        {
            return;
        }

        toast.IsDismissed = true;
        _toasts.Remove(toast);
    }

    private static TimeSpan DefaultFor(ToastKind kind) => kind switch
    {
        ToastKind.Warning => WarningDuration,
        ToastKind.Error => ErrorDuration,
        _ => DefaultDuration,
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
            dispatcher.BeginInvoke(action, DispatcherPriority.Normal);
        }
    }
}
