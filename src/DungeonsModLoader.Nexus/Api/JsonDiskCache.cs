using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DungeonsModLoader.Core;
using Microsoft.Extensions.Logging;

namespace DungeonsModLoader.Nexus.Api;

/// <summary>
/// <see cref="INexusCache"/> backed by <c>cache\nexus\&lt;sha256(key)&gt;.json</c> envelopes plus an in-memory copy.
/// Writes are atomic (temp file + move); a damaged file is treated as a miss and deleted.
/// </summary>
public sealed class JsonDiskCache : INexusCache
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    private readonly string _directory;
    private readonly ILogger<JsonDiskCache> _logger;
    private readonly ConcurrentDictionary<string, Envelope> _memory = new(StringComparer.Ordinal);

    public JsonDiskCache(AppPaths paths, ILogger<JsonDiskCache> logger)
        : this(paths.NexusCacheDirectory, logger)
    {
    }

    public JsonDiskCache(string directory, ILogger<JsonDiskCache> logger)
    {
        _directory = directory;
        _logger = logger;
    }

    public async Task<CacheHit<T>?> GetAsync<T>(string key, bool allowExpired = false, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (!_memory.TryGetValue(key, out var envelope))
        {
            envelope = await ReadAsync(key, cancellationToken).ConfigureAwait(false);
            if (envelope is null)
            {
                return null;
            }

            _memory[key] = envelope;
        }

        if (!allowExpired && envelope.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            return null;
        }

        try
        {
            var value = envelope.Payload.Deserialize<T>(Options);
            return value is null ? null : new CacheHit<T>(value, envelope.ExpiresAt, envelope.StoredAt);
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Cached value for {Key} does not match the expected shape; dropping it", key);
            await RemoveAsync(key, cancellationToken).ConfigureAwait(false);
            return null;
        }
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan timeToLive, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var now = DateTimeOffset.UtcNow;
        var envelope = new Envelope
        {
            Key = key,
            StoredAt = now,
            ExpiresAt = now + timeToLive,
            Payload = JsonSerializer.SerializeToElement(value, Options),
        };
        _memory[key] = envelope;

        try
        {
            Directory.CreateDirectory(_directory);
            var path = PathFor(key);
            var temp = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, envelope, Options, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The memory copy still serves this session; the disk copy is a bonus.
            _logger.LogDebug(ex, "Cache entry {Key} could not be written to disk", key);
        }
    }

    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        _memory.TryRemove(key, out _);
        try
        {
            var path = PathFor(key);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Cache entry {Key} could not be deleted", key);
        }

        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        _memory.Clear();
        try
        {
            if (Directory.Exists(_directory))
            {
                foreach (var file in Directory.EnumerateFiles(_directory, "*.json*"))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    File.Delete(file);
                }
            }

            _logger.LogInformation("Nexus response cache cleared");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "The Nexus response cache could not be cleared completely");
        }

        return Task.CompletedTask;
    }

    private async Task<Envelope?> ReadAsync(string key, CancellationToken cancellationToken)
    {
        var path = PathFor(key);
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
            var envelope = await JsonSerializer.DeserializeAsync<Envelope>(stream, Options, cancellationToken).ConfigureAwait(false);
            if (envelope is null || !string.Equals(envelope.Key, key, StringComparison.Ordinal))
            {
                return null;
            }

            return envelope;
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Cache file for {Key} is damaged; deleting it", key);
            await RemoveAsync(key, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Cache file for {Key} could not be read", key);
            return null;
        }
    }

    private string PathFor(string key)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        return Path.Combine(_directory, hash + ".json");
    }

    private sealed class Envelope
    {
        public string Key { get; set; } = string.Empty;
        public DateTimeOffset StoredAt { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }
        public JsonElement Payload { get; set; }
    }
}
