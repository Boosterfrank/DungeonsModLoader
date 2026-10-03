using System.Runtime.Versioning;
using Microsoft.Win32;

namespace DungeonsModLoader.Core.Game.Steam;

/// <summary>Where Steam is installed, as recorded by the Steam client. Abstracted so tests can inject a fake path.</summary>
public interface ISteamRegistry
{
    /// <summary>The Steam install folder as stored (any slash style, any casing), or <c>null</c> when Steam is not installed.</summary>
    string? GetSteamPath();
}

/// <summary>
/// Reads <c>HKCU\Software\Valve\Steam\SteamPath</c>, falling back to
/// <c>HKLM\SOFTWARE\WOW6432Node\Valve\Steam\InstallPath</c>. Never throws.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsSteamRegistry : ISteamRegistry
{
    public string? GetSteamPath()
    {
        return ReadValue(Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath")
            ?? ReadValue(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath")
            ?? ReadValue(Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath");
    }

    private static string? ReadValue(RegistryKey hive, string subKey, string valueName)
    {
        try
        {
            using var key = hive.OpenSubKey(subKey, writable: false);
            var value = key?.GetValue(valueName) as string;
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}

/// <summary>An <see cref="ISteamRegistry"/> that returns a fixed path (tests, or a user-supplied Steam folder).</summary>
public sealed class FixedSteamRegistry : ISteamRegistry
{
    private readonly string? _steamPath;

    public FixedSteamRegistry(string? steamPath)
    {
        _steamPath = steamPath;
    }

    public string? GetSteamPath() => _steamPath;
}
