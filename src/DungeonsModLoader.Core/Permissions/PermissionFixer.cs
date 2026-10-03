using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using DungeonsModLoader.Core.Game;
using Microsoft.Extensions.Logging;

namespace DungeonsModLoader.Core.Permissions;

/// <summary>
/// <inheritdoc cref="IPermissionFixer"/>
/// <para>
/// Runs a single elevated <c>cmd.exe /c</c> command (one UAC prompt) that creates <c>~mods</c> and the disabled
/// folder when missing and grants the current user modify rights on both with <c>icacls</c>. The user is
/// addressed by SID, which works in every Windows language.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PermissionFixer : IPermissionFixer
{
    /// <summary>Win32 ERROR_CANCELLED: the user dismissed the UAC prompt.</summary>
    private const int ErrorCancelled = 1223;

    private readonly ILogger<PermissionFixer> _logger;

    public PermissionFixer(ILogger<PermissionFixer> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Builds the <c>cmd.exe /c</c> command line (without the <c>/c</c>) that creates both mod folders if missing and
    /// grants <paramref name="sid"/> modify rights on them, recursively. Exposed for testing.
    /// </summary>
    public static string BuildCommand(GameInstallation installation, string sid)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentException.ThrowIfNullOrWhiteSpace(sid);

        var folders = new[] { installation.ModsDirectory, installation.DisabledModsDirectory };
        var command = new StringBuilder();

        // Folder creation first, each in its own group so a missing "if" body cannot swallow the next command;
        // the grants are chained with && so the exit code reflects the first failing icacls.
        foreach (var folder in folders)
        {
            var quoted = Quote(folder);
            command.Append("(if not exist ").Append(Quote(folder + "\\")).Append(" mkdir ").Append(quoted).Append(") & ");
        }

        for (var i = 0; i < folders.Length; i++)
        {
            if (i > 0)
            {
                command.Append(" && ");
            }

            command.Append("icacls ").Append(Quote(folders[i])).Append(" /grant *").Append(sid).Append(":(OI)(CI)M /T /Q");
        }

        return command.ToString();
    }

    public async Task<bool> GrantModifyAccessAsync(GameInstallation installation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installation);

        string sid;
        using (var identity = WindowsIdentity.GetCurrent())
        {
            sid = identity.User?.Value ?? throw new PermissionFixException("The current Windows user could not be identified.");
        }

        var command = BuildCommand(installation, sid);
        _logger.LogInformation("Requesting elevation to grant modify rights: cmd.exe /c {Command}", command);

        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c " + command,
            Verb = "runas",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = true,
        };

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            _logger.LogInformation("The user declined the elevation prompt");
            return false;
        }
        catch (Win32Exception ex)
        {
            throw new PermissionFixException("The elevated helper could not be started.", ex);
        }

        if (process is null)
        {
            throw new PermissionFixException("The elevated helper could not be started.");
        }

        using (process)
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var exitCode = process.ExitCode;
            if (exitCode != 0)
            {
                _logger.LogWarning("The elevated icacls command exited with code {ExitCode}", exitCode);
                throw new PermissionFixException($"The permission fix failed (icacls exit code {exitCode}).");
            }
        }

        _logger.LogInformation("Modify rights granted on {Mods} and {Disabled}", installation.ModsDirectory, installation.DisabledModsDirectory);
        return true;
    }

    private static string Quote(string path)
    {
        return "\"" + path + "\"";
    }
}
