using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DungeonsModLoader.App.Services;

/// <summary>Tone of a toast; decides the accent colour and the default lifetime.</summary>
public enum ToastKind
{
    Info,
    Success,
    Warning,
    Error,
}

/// <summary>One notification in the bottom-right toast stack. Created by <see cref="IToastService.Show"/>.</summary>
public sealed partial class ToastViewModel : ObservableObject
{
    private readonly IToastService _owner;
    private readonly Action? _action;

    private readonly bool _keepOpenOnAction;

    internal ToastViewModel(IToastService owner, string message, ToastKind kind, string? title, string? actionText, Action? action, bool isSticky, bool keepOpenOnAction)
    {
        _owner = owner;
        _action = action;
        _keepOpenOnAction = keepOpenOnAction;
        Message = message;
        Kind = kind;
        Title = title;
        ActionText = action is null ? null : actionText;
        IsSticky = isSticky;
    }

    public string Message { get; }

    public string? Title { get; }

    public bool HasTitle => !string.IsNullOrWhiteSpace(Title);

    public ToastKind Kind { get; }

    public bool IsInfo => Kind == ToastKind.Info;

    public bool IsSuccess => Kind == ToastKind.Success;

    public bool IsWarning => Kind == ToastKind.Warning;

    public bool IsError => Kind == ToastKind.Error;

    /// <summary>Caption of the optional action button ("Show", "Open page again").</summary>
    public string? ActionText { get; }

    public bool HasAction => ActionText is not null;

    /// <summary>Stays until dismissed by the user or the code that showed it.</summary>
    public bool IsSticky { get; }

    /// <summary>Set once the toast has left the stack (so a late Dismiss is a no-op).</summary>
    [ObservableProperty]
    private bool _isDismissed;

    [RelayCommand]
    private void Dismiss() => _owner.Dismiss(this);

    [RelayCommand]
    private void RunAction()
    {
        if (!_keepOpenOnAction)
        {
            _owner.Dismiss(this);
        }

        _action?.Invoke();
    }
}

/// <summary>
/// Bottom-right toasts / snackbar: "Installed X", "Updated 3 mods", "Waiting for Nexus Mods...", errors. Non-blocking;
/// dialogs stay for questions and for errors with details. Safe to call from any thread.
/// </summary>
public interface IToastService
{
    /// <summary>The toasts on screen, oldest first. Bound by the main window; changes on the UI thread.</summary>
    ReadOnlyObservableCollection<ToastViewModel> Toasts { get; }

    /// <summary>
    /// Shows a toast. <paramref name="duration"/> null uses the default for the kind (5 s, longer for warnings and
    /// errors); <see cref="Timeout.InfiniteTimeSpan"/> keeps it until <see cref="Dismiss"/>. The optional action
    /// button runs <paramref name="action"/> and dismisses the toast unless <paramref name="keepOpenOnAction"/> is set.
    /// </summary>
    ToastViewModel Show(string message, ToastKind kind = ToastKind.Info, string? title = null, TimeSpan? duration = null, string? actionText = null, Action? action = null, bool keepOpenOnAction = false);

    /// <summary>Removes a toast; nothing happens when it is already gone.</summary>
    void Dismiss(ToastViewModel toast);
}
