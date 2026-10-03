using Microsoft.Extensions.Logging;

namespace DungeonsModLoader.Core.Game.Xbox;

/// <summary>
/// Finds the Xbox app / Minecraft Launcher (Microsoft Store packaged) installation of the game. Candidates come
/// from the AppModel package repository (registry) and, as a fallback, from <c>{drive}\XboxGames\*\Content</c> on
/// every fixed drive. A candidate is accepted when it is a valid game root and its <c>MicrosoftGame.config</c>
/// names the game. The AppUserModelId is derived from the registry package entry when one is known. Never throws.
/// </summary>
public sealed class XboxGameLocator : IGameSourceLocator
{
    /// <summary>Folder under a drive root that the Xbox app installs games into.</summary>
    public const string XboxGamesFolderName = "XboxGames";

    private const string IdentityNameFragment = "MinecraftDungeons";

    private readonly IXboxPackageRepository _packages;
    private readonly IReadOnlyList<string>? _xboxGamesFolders;
    private readonly ILogger<XboxGameLocator> _logger;

    public XboxGameLocator(IXboxPackageRepository packages, ILogger<XboxGameLocator> logger)
    {
        _packages = packages;
        _logger = logger;
    }

    /// <summary>
    /// Scans <paramref name="xboxGamesFolders"/> (folders shaped like <c>C:\XboxGames</c>) instead of every fixed
    /// drive (tests).
    /// </summary>
    public XboxGameLocator(IXboxPackageRepository packages, IReadOnlyList<string> xboxGamesFolders, ILogger<XboxGameLocator> logger)
        : this(packages, logger)
    {
        _xboxGamesFolders = xboxGamesFolders;
    }

    public GameSource Source => GameSource.Xbox;

    public Task<IReadOnlyList<GameInstallation>> LocateAsync(CancellationToken cancellationToken = default)
        => Task.Run(() => Locate(cancellationToken), cancellationToken);

    /// <summary>Synchronous detection (registry + file I/O); prefer <see cref="LocateAsync"/> from UI code.</summary>
    public IReadOnlyList<GameInstallation> Locate(CancellationToken cancellationToken = default)
    {
        var results = new List<GameInstallation>();
        try
        {
            var packages = GetPackages();

            // 1. Registry: every installed package whose root (or root\Content) is the game.
            foreach (var package in packages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var candidate in new[] { package.PackageRootFolder, Path.Combine(package.PackageRootFolder, "Content") })
                {
                    TryAccept(candidate, package, packages, results);
                }
            }

            // 2. Fallback: <drive>\XboxGames\<Game>\Content, for installs the repository does not list.
            foreach (var xboxGames in GetXboxGamesFolders())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Directory.Exists(xboxGames))
                {
                    continue;
                }

                IEnumerable<string> gameFolders;
                try
                {
                    gameFolders = Directory.EnumerateDirectories(xboxGames).ToList();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogDebug(ex, "Could not list {Folder}", xboxGames);
                    continue;
                }

                foreach (var gameFolder in gameFolders)
                {
                    TryAccept(Path.Combine(gameFolder, "Content"), package: null, packages, results);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Xbox app detection failed; continuing without Xbox results");
        }

        return results;
    }

    /// <summary>True when the config describes this game (display name match, or identity contains "MinecraftDungeons").</summary>
    public static bool Matches(MicrosoftGameConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return string.Equals(config.DefaultDisplayName, AppInfo.GameDisplayName, StringComparison.OrdinalIgnoreCase)
            || (config.IdentityName?.Contains(IdentityNameFragment, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private void TryAccept(string candidate, XboxPackage? package, IReadOnlyList<XboxPackage> packages, List<GameInstallation> results)
    {
        var root = GamePaths.TryNormalize(candidate);
        if (root is null || results.Any(existing => string.Equals(existing.Root, root, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        if (!GameInstallation.IsValidRoot(root))
        {
            return;
        }

        var config = MicrosoftGameConfig.TryLoad(Path.Combine(root, MicrosoftGameConfig.FileName));
        if (config is null)
        {
            _logger.LogDebug("{Root} looks like a game root but has no readable {File}; skipping", root, MicrosoftGameConfig.FileName);
            return;
        }

        if (!Matches(config))
        {
            _logger.LogDebug("{Root} is a different game ({Name}); skipping", root, config.DefaultDisplayName ?? config.IdentityName);
            return;
        }

        // A folder found by scanning may still belong to a registered package: match it by identity name so the
        // AppUserModelId (and therefore launching through the Xbox app) keeps working.
        package ??= FindPackageByIdentity(packages, config.IdentityName);

        var appUserModelId = package is null
            ? null
            : PackageIdentity.GetAppUserModelId(package.Value.PackageFullName, config.MainExecutableId);

        if (appUserModelId is null)
        {
            _logger.LogInformation("Found Xbox app install of {Game} at {Root} without a package identity; it will be launched from its executable", AppInfo.GameDisplayName, root);
        }
        else
        {
            _logger.LogInformation("Found Xbox app install of {Game} at {Root} (AppUserModelId {Aumid})", AppInfo.GameDisplayName, root, appUserModelId);
        }

        results.Add(new GameInstallation
        {
            Root = root,
            Source = GameSource.Xbox,
            XboxAppUserModelId = appUserModelId,
            ExecutableNames = GameInstallation.DiscoverExecutableNames(root),
        });
    }

    private static XboxPackage? FindPackageByIdentity(IReadOnlyList<XboxPackage> packages, string? identityName)
    {
        if (string.IsNullOrWhiteSpace(identityName))
        {
            return null;
        }

        foreach (var package in packages)
        {
            if (string.Equals(PackageIdentity.GetName(package.PackageFullName), identityName, StringComparison.OrdinalIgnoreCase))
            {
                return package;
            }
        }

        return null;
    }

    private IReadOnlyList<XboxPackage> GetPackages()
    {
        try
        {
            return _packages.GetInstalledPackages();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not list installed packaged apps");
            return Array.Empty<XboxPackage>();
        }
    }

    private IEnumerable<string> GetXboxGamesFolders()
    {
        if (_xboxGamesFolders is not null)
        {
            return _xboxGamesFolders;
        }

        var folders = new List<string>();
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (drive.DriveType == DriveType.Fixed && drive.IsReady)
                    {
                        folders.Add(Path.Combine(drive.RootDirectory.FullName, XboxGamesFolderName));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A drive that cannot be queried is simply not scanned.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not enumerate drives for the XboxGames scan");
        }

        return folders;
    }
}
