using System.Windows;
using System.Windows.Threading;
using DungeonsModLoader.App.ViewModels.Dialogs;
using DungeonsModLoader.App.Views.Dialogs;
using DungeonsModLoader.Core.Mods;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DungeonsModLoader.App.Services;

/// <summary>Shows the "Link mods to Nexus Mods" dialog for a set of local mods.</summary>
public interface ILinkModsPrompt
{
    /// <summary>Shows the dialog modally and returns how many mods were linked.</summary>
    Task<int> ShowAsync(IReadOnlyList<ModInfo> mods);
}

/// <inheritdoc cref="ILinkModsPrompt"/>
public sealed class LinkModsPrompt : ILinkModsPrompt
{
    private readonly IServiceProvider _services;
    private readonly ILogger<LinkModsPrompt> _logger;
    private bool _open;

    public LinkModsPrompt(IServiceProvider services, ILogger<LinkModsPrompt> logger)
    {
        _services = services;
        _logger = logger;
    }

    public Task<int> ShowAsync(IReadOnlyList<ModInfo> mods)
    {
        ArgumentNullException.ThrowIfNull(mods);
        var dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        return dispatcher.CheckAccess()
            ? Task.FromResult(ShowCore(mods))
            : dispatcher.InvokeAsync(() => ShowCore(mods), DispatcherPriority.Normal).Task;
    }

    private int ShowCore(IReadOnlyList<ModInfo> mods)
    {
        if (_open)
        {
            _logger.LogDebug("Link dialog already open; not opening a second one");
            return 0;
        }

        _open = true;
        try
        {
            var viewModel = _services.GetRequiredService<LinkModsViewModel>();
            using (viewModel)
            {
                viewModel.Load(mods);
                var dialog = new LinkModsDialog(viewModel);
                ModalDialogs.ShowModal(dialog);
                _logger.LogInformation("Link dialog closed: {Linked} of {Count} mod(s) linked", viewModel.LinkedCount, mods.Count);
                return viewModel.LinkedCount;
            }
        }
        finally
        {
            _open = false;
        }
    }
}
