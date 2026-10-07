using System.IO;
using System.Diagnostics;
using System.Runtime.Versioning;
using DungeonsModLoader.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace DungeonsModLoader.App.Services;

/// <summary>Who handles <c>nxm://</c> links on this PC right now.</summary>
/// <param name="Command">The registered command line, when any.</param>
public sealed record NxmHandlerState(bool IsRegistered, bool IsThisApp, string? Command)
{
    public bool IsOtherApp => IsRegistered && !IsThisApp;

    /// <summary>"Vortex" from <c>"C:\Program Files\Vortex\Vortex.exe" -d "%1"</c>; null when nothing is registered.</summary>
    public string? HandlerName
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Command))
            {
                return null;
            }

            var trimmed = Command.Trim();
            var exe = trimmed.StartsWith('"') ? trimmed[1..].Split('"', 2)[0] : trimmed.Split(' ', 2)[0];
            var name = Path.GetFileNameWithoutExtension(exe);
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
    }
}

/// <summary>
/// Per-user registration of this app as the <c>nxm://</c> protocol handler (<c>HKCU\Software\Classes\nxm</c>), so
/// the website's "Mod Manager Download" button opens downloads here. No elevation needed; unregistering only
/// removes the key when it still points to this app.
/// </summary>
public interface INxmProtocolRegistration
{
    NxmHandlerState GetState();

    void Register();

    void Unregister();
}

[SupportedOSPlatform("windows")]
public sealed class NxmProtocolRegistration : INxmProtocolRegistration
{
    private const string ProtocolKey = @"Software\Classes\nxm";
    private const string CommandKey = ProtocolKey + @"\shell\open\command";
    private const string IconKey = ProtocolKey + @"\DefaultIcon";

    private readonly ILogger<NxmProtocolRegistration> _logger;

    public NxmProtocolRegistration(ILogger<NxmProtocolRegistration> logger)
    {
        _logger = logger;
    }

    public NxmHandlerState GetState()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(CommandKey);
            var command = key?.GetValue(null) as string;
            if (string.IsNullOrWhiteSpace(command))
            {
                return new NxmHandlerState(false, false, null);
            }

            var isThisApp = command.Contains(ExePath, StringComparison.OrdinalIgnoreCase);
            return new NxmHandlerState(true, isThisApp, command);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            _logger.LogWarning(ex, "The nxm:// handler registration could not be read");
            return new NxmHandlerState(false, false, null);
        }
    }

    public void Register()
    {
        var exe = ExePath;
        using (var key = Registry.CurrentUser.CreateSubKey(ProtocolKey, writable: true))
        {
            key.SetValue(null, "URL:Nexus Mods Protocol");
            key.SetValue("URL Protocol", string.Empty);
            key.SetValue("Registered by", AppInfo.DisplayName);
        }

        using (var icon = Registry.CurrentUser.CreateSubKey(IconKey, writable: true))
        {
            icon.SetValue(null, $"\"{exe}\",0");
        }

        using (var command = Registry.CurrentUser.CreateSubKey(CommandKey, writable: true))
        {
            command.SetValue(null, $"\"{exe}\" \"%1\"");
        }

        _logger.LogInformation("Registered as the nxm:// handler ({Exe})", exe);
    }

    public void Unregister()
    {
        var state = GetState();
        if (!state.IsThisApp)
        {
            _logger.LogInformation("Not removing the nxm:// handler: it points to {Command}", state.Command ?? "(nothing)");
            return;
        }

        Registry.CurrentUser.DeleteSubKeyTree(ProtocolKey, throwOnMissingSubKey: false);
        _logger.LogInformation("Removed the nxm:// handler registration");
    }

    private static string ExePath =>
        Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? throw new InvalidOperationException("The application path is unknown.");
}
