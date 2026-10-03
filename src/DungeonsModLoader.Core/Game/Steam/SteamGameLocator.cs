using Microsoft.Extensions.Logging;

namespace DungeonsModLoader.Core.Game.Steam;

/// <summary>
/// Finds the Steam installation of the game: resolves the Steam folder (registry or injected), collects every
/// Steam library from <c>steamapps\libraryfolders.vdf</c> (plus the Steam folder itself), and picks the
/// <c>appmanifest_*.acf</c> whose <c>installdir</c> is <see cref="AppInfo.GameDisplayName"/>. Never throws.
/// </summary>
public sealed class SteamGameLocator : IGameSourceLocator
{
    private readonly ISteamRegistry _registry;
    private readonly ILogger<SteamGameLocator> _logger;

    public SteamGameLocator(ISteamRegistry registry, ILogger<SteamGameLocator> logger)
    {
        _registry = registry;
        _logger = logger;
    }

    /// <summary>Uses <paramref name="steamRoot"/> as the Steam folder instead of reading the registry.</summary>
    public SteamGameLocator(string? steamRoot, ILogger<SteamGameLocator> logger)
        : this(new FixedSteamRegistry(steamRoot), logger)
    {
    }

    public GameSource Source => GameSource.Steam;

    public Task<IReadOnlyList<GameInstallation>> LocateAsync(CancellationToken cancellationToken = default)
        => Task.Run(() => Locate(cancellationToken), cancellationToken);

    /// <summary>Synchronous detection (file I/O); prefer <see cref="LocateAsync"/> from UI code.</summary>
    public IReadOnlyList<GameInstallation> Locate(CancellationToken cancellationToken = default)
    {
        var results = new List<GameInstallation>();
        try
        {
            var steamRoot = ResolveSteamRoot();
            if (steamRoot is null)
            {
                return results;
            }

            foreach (var library in GetLibraryPaths(steamRoot))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var installation = FindInLibrary(library);
                if (installation is not null && !results.Any(existing => GamePaths.AreSameFolder(existing.Root, installation.Root)))
                {
                    _logger.LogInformation("Found Steam install of {Game} at {Root} (app id {AppId})", AppInfo.GameDisplayName, installation.Root, installation.SteamAppId);
                    results.Add(installation);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Steam detection failed; continuing without Steam results");
        }

        return results;
    }

    /// <summary>
    /// Every Steam library folder (the ones that contain a <c>steamapps</c> folder): the entries of
    /// <c>steamapps\libraryfolders.vdf</c> (new numbered-block format or the old flat <c>"1" "path"</c> format,
    /// with <c>config\libraryfolders.vdf</c> as a fallback location) followed by <paramref name="steamRoot"/> itself.
    /// Normalized and de-duplicated; folders that do not exist are dropped.
    /// </summary>
    public IReadOnlyList<string> GetLibraryPaths(string steamRoot)
    {
        var libraries = new List<string>();

        void Add(string? candidate)
        {
            var normalized = GamePaths.TryNormalize(candidate);
            if (normalized is null || libraries.Any(existing => string.Equals(existing, normalized, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            if (!Directory.Exists(normalized))
            {
                _logger.LogDebug("Steam library {Library} does not exist; skipping", normalized);
                return;
            }

            libraries.Add(normalized);
        }

        foreach (var vdfPath in new[] { Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf"), Path.Combine(steamRoot, "config", "libraryfolders.vdf") })
        {
            if (!File.Exists(vdfPath))
            {
                continue;
            }

            try
            {
                var document = VdfParser.ParseFile(vdfPath);
                var top = document.Find("libraryfolders") ?? document.Children.FirstOrDefault(child => child.IsBlock);
                if (top is null)
                {
                    continue;
                }

                foreach (var entry in top.Children)
                {
                    if (entry.IsBlock)
                    {
                        Add(entry.GetValue("path")); // new format: "0" { "path" "..." ... }
                    }
                    else if (entry.Name.All(char.IsAsciiDigit))
                    {
                        Add(entry.Value); // old format: "1" "D:\\SteamLibrary"
                    }
                }

                break; // the first file that parses wins
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not read {Path}", vdfPath);
            }
        }

        Add(steamRoot);
        return libraries;
    }

    private string? ResolveSteamRoot()
    {
        string? raw;
        try
        {
            raw = _registry.GetSteamPath();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the Steam install path");
            return null;
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            _logger.LogDebug("Steam is not installed (no Steam path recorded)");
            return null;
        }

        var normalized = GamePaths.TryNormalize(raw);
        if (normalized is null || !Directory.Exists(normalized))
        {
            _logger.LogDebug("Steam path {Path} does not exist", raw);
            return null;
        }

        return normalized;
    }

    private GameInstallation? FindInLibrary(string library)
    {
        var steamApps = Path.Combine(library, "steamapps");
        if (!Directory.Exists(steamApps))
        {
            return null;
        }

        IEnumerable<string> manifests;
        try
        {
            manifests = Directory.EnumerateFiles(steamApps, "appmanifest_*.acf", SearchOption.TopDirectoryOnly).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not list app manifests in {Folder}", steamApps);
            return null;
        }

        foreach (var manifestPath in manifests)
        {
            try
            {
                var document = VdfParser.ParseFile(manifestPath);
                var appState = document.Find("AppState") ?? document.Children.FirstOrDefault(child => child.IsBlock);
                if (appState is null)
                {
                    continue;
                }

                var installDir = appState.GetValue("installdir");
                var name = appState.GetValue("name");
                var matches = string.Equals(installDir, AppInfo.GameDisplayName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, AppInfo.GameDisplayName, StringComparison.OrdinalIgnoreCase);
                if (!matches || string.IsNullOrWhiteSpace(installDir))
                {
                    continue;
                }

                var root = GamePaths.TryNormalize(Path.Combine(steamApps, "common", installDir));
                if (root is null || !GameInstallation.IsValidRoot(root))
                {
                    _logger.LogDebug("Manifest {Manifest} points at {Root}, which is not a valid game root", manifestPath, root);
                    continue;
                }

                var appId = appState.GetValue("appid");
                if (string.IsNullOrWhiteSpace(appId))
                {
                    _logger.LogWarning("Manifest {Manifest} has no appid; Steam launching will not work for it", manifestPath);
                }

                return new GameInstallation
                {
                    Root = root,
                    Source = GameSource.Steam,
                    SteamAppId = string.IsNullOrWhiteSpace(appId) ? null : appId.Trim(),
                    ExecutableNames = GameInstallation.DiscoverExecutableNames(root),
                };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not read {Manifest}", manifestPath);
            }
        }

        return null;
    }
}
