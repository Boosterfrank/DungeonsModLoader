using Microsoft.Extensions.Logging;

namespace DungeonsModLoader.Core.Game;

/// <summary>
/// The <see cref="IGameLocator"/> of the app: asks every <see cref="IGameSourceLocator"/> (Steam, then Xbox) on a
/// background thread, isolating each so one failing never hides the others, and de-duplicates by normalized root.
/// Only cancellation is propagated; every other failure is logged and skipped.
/// </summary>
public sealed class CompositeGameLocator : IGameLocator
{
    private readonly IReadOnlyList<IGameSourceLocator> _sources;
    private readonly ILogger<CompositeGameLocator> _logger;

    public CompositeGameLocator(IEnumerable<IGameSourceLocator> sources, ILogger<CompositeGameLocator> logger)
    {
        ArgumentNullException.ThrowIfNull(sources);
        _sources = sources.OrderBy(source => Rank(source.Source)).ToList();
        _logger = logger;
    }

    public async Task<IReadOnlyList<GameInstallation>> LocateAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<GameInstallation>();
        foreach (var source in _sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<GameInstallation> found;
            try
            {
                found = await Task.Run(() => source.LocateAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "{Source} detection failed; continuing with the other sources", source.Source);
                continue;
            }

            foreach (var installation in found)
            {
                if (installation is null || GamePaths.TryNormalize(installation.Root) is not { } root)
                {
                    continue;
                }

                if (results.Any(existing => string.Equals(existing.Root, root, StringComparison.OrdinalIgnoreCase)))
                {
                    _logger.LogDebug("{Source} reported {Root}, which was already found; keeping the first result", source.Source, root);
                    continue;
                }

                results.Add(string.Equals(installation.Root, root, StringComparison.Ordinal) ? installation : installation with { Root = root });
            }
        }

        if (results.Count == 0)
        {
            _logger.LogInformation("No {Game} installation was found automatically", AppInfo.GameDisplayName);
        }

        return results;
    }

    public async Task<GameInstallation?> IdentifyAsync(string root, CancellationToken cancellationToken = default)
    {
        var normalized = GamePaths.TryNormalize(root);
        if (normalized is null)
        {
            return null;
        }

        var located = await LocateAsync(cancellationToken).ConfigureAwait(false);
        var known = located.FirstOrDefault(installation => string.Equals(installation.Root, normalized, StringComparison.OrdinalIgnoreCase));
        if (known is not null)
        {
            _logger.LogDebug("{Root} is the known {Source} install", normalized, known.Source);
            return known;
        }

        if (!GameInstallation.IsValidRoot(normalized))
        {
            _logger.LogDebug("{Root} is not a game root (no Dungeons\\Content\\Paks)", normalized);
            return null;
        }

        _logger.LogInformation("{Root} is a valid game root picked by hand; treating it as a manual install", normalized);
        return new GameInstallation
        {
            Root = normalized,
            Source = GameSource.Manual,
            ExecutableNames = GameInstallation.DiscoverExecutableNames(normalized),
        };
    }

    private static int Rank(GameSource source) => source switch
    {
        GameSource.Steam => 0,
        GameSource.Xbox => 1,
        _ => 2,
    };
}
