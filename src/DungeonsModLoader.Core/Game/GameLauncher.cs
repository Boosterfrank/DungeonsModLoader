using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace DungeonsModLoader.Core.Game;

/// <summary>
/// Starts the game the way its source expects:
/// <list type="bullet">
///   <item>Steam: ShellExecute of <c>steam://rungameid/{SteamAppId}</c> (Steam applies its launch options and overlay).</item>
///   <item>Xbox app / Minecraft Launcher: ShellExecute of <c>shell:AppsFolder\{AppUserModelId}</c>, which the shell
///   resolves to the packaged app's activation; if the shell refuses the direct form, the same target is handed to
///   <c>explorer.exe</c>. Without an AppUserModelId the executable is started directly.</item>
///   <item>Manual: starts <see cref="GameInstallation.FindLaunchExecutable"/> with its folder as working directory.</item>
/// </list>
/// Every failure surfaces as a <see cref="GameLaunchException"/> whose message is safe to show.
/// </summary>
public sealed class GameLauncher : IGameLauncher
{
    private readonly ILogger<GameLauncher> _logger;

    public GameLauncher(ILogger<GameLauncher> logger)
    {
        _logger = logger;
    }

    public Task LaunchAsync(GameInstallation installation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installation);
        cancellationToken.ThrowIfCancellationRequested();

        // Process.Start blocks for a moment (shell resolution, UAC); keep the UI thread free.
        return Task.Run(() => Launch(installation), cancellationToken);
    }

    private void Launch(GameInstallation installation)
    {
        switch (installation.Source)
        {
            case GameSource.Steam:
                LaunchSteam(installation);
                break;
            case GameSource.Xbox:
                LaunchXbox(installation);
                break;
            default:
                LaunchExecutable(installation);
                break;
        }
    }

    private void LaunchSteam(GameInstallation installation)
    {
        if (string.IsNullOrWhiteSpace(installation.SteamAppId))
        {
            _logger.LogWarning("Steam install at {Root} has no app id; starting the executable directly instead", installation.Root);
            LaunchExecutable(installation);
            return;
        }

        var uri = "steam://rungameid/" + installation.SteamAppId.Trim();
        _logger.LogInformation("Launching {Game} through Steam: {Target}", AppInfo.GameDisplayName, uri);
        try
        {
            StartShell(uri);
        }
        catch (Exception ex) when (IsStartFailure(ex))
        {
            _logger.LogError(ex, "Steam launch failed for {Target}", uri);
            throw new GameLaunchException("Steam is not installed or the steam:// protocol is not registered. Open Steam and try again, or switch to launching the game directly in Settings.", ex);
        }
    }

    private void LaunchXbox(GameInstallation installation)
    {
        if (string.IsNullOrWhiteSpace(installation.XboxAppUserModelId))
        {
            _logger.LogInformation("Xbox app install at {Root} has no AppUserModelId; starting the executable directly instead", installation.Root);
            LaunchExecutable(installation);
            return;
        }

        var target = @"shell:AppsFolder\" + installation.XboxAppUserModelId.Trim();
        _logger.LogInformation("Launching {Game} through the Xbox app: {Target}", AppInfo.GameDisplayName, target);
        try
        {
            StartShell(target);
            return;
        }
        catch (Exception ex) when (IsStartFailure(ex))
        {
            _logger.LogWarning(ex, "Direct shell activation of {Target} failed; retrying through explorer.exe", target);
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo("explorer.exe", target) { UseShellExecute = true });
        }
        catch (Exception ex) when (IsStartFailure(ex))
        {
            _logger.LogError(ex, "Xbox app launch failed for {Target}", target);
            throw new GameLaunchException("The Xbox app could not start the game. Check that it is still installed in the Xbox app or the Minecraft Launcher, or switch to launching the game directly in Settings.", ex);
        }
    }

    private void LaunchExecutable(GameInstallation installation)
    {
        var executable = installation.FindLaunchExecutable()
            ?? throw new GameLaunchException($"Could not find the game executable in {installation.Root}.");

        var workingDirectory = Path.GetDirectoryName(executable) ?? installation.Root;
        _logger.LogInformation("Launching {Game} from its executable: {Executable}", AppInfo.GameDisplayName, executable);
        try
        {
            using var process = Process.Start(new ProcessStartInfo(executable)
            {
                WorkingDirectory = workingDirectory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex) when (IsStartFailure(ex))
        {
            _logger.LogError(ex, "Starting {Executable} failed", executable);
            throw new GameLaunchException($"Windows could not start {Path.GetFileName(executable)}. Check that the game files are intact and that nothing is blocking the executable.", ex);
        }
    }

    private static void StartShell(string target)
    {
        // UseShellExecute routes protocol URIs (steam://) and shell: paths through ShellExecuteEx; no process
        // handle comes back for those, so the (possibly null) result is just disposed.
        using var process = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
    }

    private static bool IsStartFailure(Exception ex)
        => ex is Win32Exception or IOException or InvalidOperationException or UnauthorizedAccessException or PlatformNotSupportedException;
}
