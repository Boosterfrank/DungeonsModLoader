using System.Runtime.Versioning;
using Microsoft.Win32;

namespace DungeonsModLoader.Core.Game.Xbox;

/// <summary>An installed packaged app (Xbox app / Microsoft Store), as listed in the AppModel package repository.</summary>
/// <param name="PackageFullName">E.g. <c>Microsoft.MinecraftDungeons2_1.1.1.0_x64__8wekyb3d8bbwe</c>.</param>
/// <param name="PackageRootFolder">The install folder, e.g. <c>C:\XboxGames\Minecraft Dungeons II\Content</c>.</param>
public readonly record struct XboxPackage(string PackageFullName, string PackageRootFolder);

/// <summary>Lists installed packaged apps. Abstracted so tests can fake the registry.</summary>
public interface IXboxPackageRepository
{
    /// <summary>Every installed package with a known root folder. Never throws; empty on failure.</summary>
    IReadOnlyList<XboxPackage> GetInstalledPackages();
}

/// <summary>
/// Reads the per-user package repository:
/// <c>HKCU\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages\{PackageFullName}</c>
/// with its <c>PackageRootFolder</c> value.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsXboxPackageRepository : IXboxPackageRepository
{
    public const string PackagesKeyPath = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    public IReadOnlyList<XboxPackage> GetInstalledPackages()
    {
        var packages = new List<XboxPackage>();
        try
        {
            using var packagesKey = Registry.CurrentUser.OpenSubKey(PackagesKeyPath, writable: false);
            if (packagesKey is null)
            {
                return packages;
            }

            foreach (var fullName in packagesKey.GetSubKeyNames())
            {
                try
                {
                    using var packageKey = packagesKey.OpenSubKey(fullName, writable: false);
                    if (packageKey?.GetValue("PackageRootFolder") is string root && !string.IsNullOrWhiteSpace(root))
                    {
                        packages.Add(new XboxPackage(fullName, root));
                    }
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
                {
                    // One unreadable package must not hide the others.
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // No access to the repository: behave as if nothing is installed.
        }

        return packages;
    }
}

/// <summary>An <see cref="IXboxPackageRepository"/> over a fixed list (tests).</summary>
public sealed class FixedXboxPackageRepository : IXboxPackageRepository
{
    private readonly IReadOnlyList<XboxPackage> _packages;

    public FixedXboxPackageRepository(params XboxPackage[] packages)
    {
        _packages = packages;
    }

    public FixedXboxPackageRepository(IEnumerable<XboxPackage> packages)
    {
        _packages = packages.ToList();
    }

    public IReadOnlyList<XboxPackage> GetInstalledPackages() => _packages;
}
