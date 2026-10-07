using System.Windows;
using System.Windows.Threading;
using DungeonsModLoader.App.ViewModels.Dialogs;
using DungeonsModLoader.App.Views.Dialogs;
using DungeonsModLoader.Nexus.Auth;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DungeonsModLoader.App.Services;

/// <summary>
/// The one place where a Nexus Mods account gets connected: a guided dialog (open the key page, copy the personal
/// key, paste, Connect). Used by the Browse page, the install flow (a download without a key starts here and then
/// continues), the Installed page's update check, Settings and the first-run wizard.
/// </summary>
public interface INexusConnectPrompt
{
    /// <summary>
    /// Returns true right away when a key is stored; otherwise shows the dialog with <paramref name="reason"/> on
    /// top ("Downloads use your Nexus Mods account.") and returns whether a key is stored afterwards.
    /// </summary>
    Task<bool> EnsureConnectedAsync(string? reason = null);

    /// <summary>Always shows the dialog (the Settings page's Connect button); returns true when a key was stored.</summary>
    Task<bool> ShowAsync(string? reason = null);
}

/// <inheritdoc cref="INexusConnectPrompt"/>
public sealed class NexusConnectPrompt : INexusConnectPrompt
{
    private readonly IServiceProvider _services;
    private readonly INexusSession _session;
    private readonly ILogger<NexusConnectPrompt> _logger;
    private bool _open;

    public NexusConnectPrompt(IServiceProvider services, INexusSession session, ILogger<NexusConnectPrompt> logger)
    {
        _services = services;
        _session = session;
        _logger = logger;
    }

    public Task<bool> EnsureConnectedAsync(string? reason = null) =>
        _session.HasApiKey ? Task.FromResult(true) : ShowAsync(reason);

    public Task<bool> ShowAsync(string? reason = null)
    {
        var dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        return dispatcher.CheckAccess()
            ? Task.FromResult(ShowCore(reason))
            : dispatcher.InvokeAsync(() => ShowCore(reason), DispatcherPriority.Normal).Task;
    }

    private bool ShowCore(string? reason)
    {
        if (_open)
        {
            // A second request while the dialog is up (an nxm link arriving mid-flow): answer with the current state.
            _logger.LogDebug("Connect dialog already open; not opening a second one");
            return _session.HasApiKey;
        }

        _open = true;
        try
        {
            var viewModel = _services.GetRequiredService<NexusConnectViewModel>();
            viewModel.Reason = reason;
            var dialog = new NexusConnectDialog(viewModel);
            ModalDialogs.ShowModal(dialog);
            _logger.LogInformation("Connect dialog closed ({Outcome})", viewModel.Connected ? "connected" : "not connected");
            return _session.HasApiKey;
        }
        finally
        {
            _open = false;
        }
    }
}
