namespace DungeonsModLoader.Nexus.Api;

/// <summary>
/// Disk + memory cache for API responses. Every entry has an expiry; expired entries can still be read as a
/// fallback when Nexus is unreachable (<see cref="GetAsync{T}"/> with <c>allowExpired</c>).
/// </summary>
public interface INexusCache
{
    Task<CacheHit<T>?> GetAsync<T>(string key, bool allowExpired = false, CancellationToken cancellationToken = default);

    Task SetAsync<T>(string key, T value, TimeSpan timeToLive, CancellationToken cancellationToken = default);

    Task RemoveAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Deletes every cached response (Settings: "Clear cache").</summary>
    Task ClearAsync(CancellationToken cancellationToken = default);
}

/// <summary>A cached value and whether it is still fresh.</summary>
public sealed record CacheHit<T>(T Value, DateTimeOffset ExpiresAt, DateTimeOffset StoredAt)
{
    public bool IsExpired => ExpiresAt <= DateTimeOffset.UtcNow;
}
