using DungeonsModLoader.Core.Game;

namespace DungeonsModLoader.Core.Permissions;

/// <summary>
/// One-time elevated helper that grants the current user modify rights on <c>~mods</c> and the disabled folder,
/// so the app itself never needs to run as administrator.
/// </summary>
public interface IPermissionFixer
{
    /// <summary>
    /// Runs an elevated <c>icacls</c> (UAC prompt) granting the current user modify rights on both mod folders
    /// (creating them first). Returns false when the user cancelled the elevation prompt; throws
    /// <see cref="PermissionFixException"/> when the command ran but failed.
    /// </summary>
    Task<bool> GrantModifyAccessAsync(GameInstallation installation, CancellationToken cancellationToken = default);
}

public sealed class PermissionFixException : Exception
{
    public PermissionFixException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
