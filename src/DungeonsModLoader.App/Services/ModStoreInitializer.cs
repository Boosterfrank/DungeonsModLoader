using DungeonsModLoader.Core;
using DungeonsModLoader.Core.Game;
using DungeonsModLoader.Core.Mods;
using DungeonsModLoader.Core.Permissions;
using Microsoft.Extensions.Logging;

namespace DungeonsModLoader.App.Services;

/// <summary>
/// The one place that turns mod-store initialization problems into user-facing flows: an access-denied error
/// offers the one-time elevated permission fix and retries; anything else becomes a friendly error dialog.
/// Used at startup, by the setup wizard, when the game folder changes in Settings, and before launching.
/// </summary>
public interface IModStoreInitializer
{
    /// <summary>
    /// Runs <see cref="IModService.InitializeAsync"/> for the current game installation. Returns true when the
    /// store is ready; false when it is not (the user declined or cancelled the permission fix, or an error was
    /// shown). Never throws for expected failures.
    /// </summary>
    Task<bool> InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Offers the elevated permission fix for the current installation (dialog + UAC). Returns true when it ran
    /// successfully; false when the user declined, cancelled the elevation prompt, or the fix failed (an error
    /// dialog is shown in that last case).
    /// </summary>
    Task<bool> TryFixPermissionsAsync(string? deniedPath, CancellationToken cancellationToken = default);
}

public sealed class ModStoreInitializer : IModStoreInitializer
{
    private readonly IModService _mods;
    private readonly IGameContext _game;
    private readonly IPermissionFixer _permissions;
    private readonly IDialogService _dialogs;
    private readonly ILogger<ModStoreInitializer> _logger;

    public ModStoreInitializer(
        IModService mods,
        IGameContext game,
        IPermissionFixer permissions,
        IDialogService dialogs,
        ILogger<ModStoreInitializer> logger)
    {
        _mods = mods;
        _game = game;
        _permissions = permissions;
        _dialogs = dialogs;
        _logger = logger;
    }

    public async Task<bool> InitializeAsync(CancellationToken cancellationToken = default)
    {
        var installation = _game.Current;
        if (installation is null)
        {
            return false;
        }

        var retried = false;
        while (true)
        {
            try
            {
                await _mods.InitializeAsync(cancellationToken);
                return true;
            }
            catch (ModAccessDeniedException denied)
            {
                _logger.LogWarning(denied, "Access denied while preparing the mod folders at {Path}", denied.Path);
                if (!retried && await TryFixPermissionsAsync(denied.Path, cancellationToken))
                {
                    retried = true;
                    continue;
                }

                await _dialogs.ShowErrorAsync(
                    "Mod folders not accessible",
                    $"Windows did not allow {AppInfo.DisplayName} to use the mod folders in{Environment.NewLine}{denied.Path}{Environment.NewLine}{Environment.NewLine}"
                    + "Mods cannot be managed until the folder permissions are fixed. You can retry from the Installed page.",
                    denied.ToString());
                return false;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "The mod store could not be prepared for {Root}", installation.Root);
                await _dialogs.ShowErrorAsync(
                    "Mod list unavailable",
                    $"The mod list could not be prepared for{Environment.NewLine}{installation.Root}{Environment.NewLine}{Environment.NewLine}"
                    + "Nothing was changed. Make sure the game folder and the app data folder are accessible, then retry from the Installed page.",
                    ex.ToString());
                return false;
            }
        }
    }

    public async Task<bool> TryFixPermissionsAsync(string? deniedPath, CancellationToken cancellationToken = default)
    {
        var installation = _game.Current;
        if (installation is null)
        {
            return false;
        }

        var location = deniedPath ?? installation.ModsDirectory;
        var fix = await _dialogs.ConfirmAsync(
            "Permission needed",
            $"Windows did not allow changes to the mod folder:{Environment.NewLine}{location}{Environment.NewLine}{Environment.NewLine}"
            + $"{AppInfo.DisplayName} can give your account permission to change the mod folders. Windows will ask for administrator approval once.",
            "Fix permissions");
        if (!fix)
        {
            _logger.LogInformation("Permission fix declined");
            return false;
        }

        try
        {
            var granted = await _permissions.GrantModifyAccessAsync(installation, cancellationToken);
            _logger.LogInformation(granted ? "Permission fix applied" : "Permission fix cancelled at the elevation prompt");
            return granted;
        }
        catch (PermissionFixException ex)
        {
            _logger.LogError(ex, "Permission fix failed");
            await _dialogs.ShowErrorAsync(
                "Could not fix permissions",
                "The permission change did not complete. Try again, or give your account modify rights on the game's Paks folder by hand.",
                ex.ToString());
            return false;
        }
    }
}
