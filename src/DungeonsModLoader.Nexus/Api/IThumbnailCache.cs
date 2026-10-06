using System.Security.Cryptography;
using System.Text;
using DungeonsModLoader.Core;
using Microsoft.Extensions.Logging;

namespace DungeonsModLoader.Nexus.Api;

/// <summary>Downloads mod thumbnails once and serves them from <c>cache\thumbnails\</c>.</summary>
public interface IThumbnailCache
{
    /// <summary>Local file path of the image, downloading it when needed; null when the URL is unusable or the download failed.</summary>
    Task<string?> GetFileAsync(string? url, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IThumbnailCache"/>
public sealed class ThumbnailCache : IThumbnailCache
{
    /// <summary>Thumbnails rarely change; a week keeps browsing cheap while picking up replaced images eventually.</summary>
    private static readonly TimeSpan TimeToLive = TimeSpan.FromDays(7);
    private const long MaxBytes = 8 * 1024 * 1024;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _directory;
    private readonly ILogger<ThumbnailCache> _logger;
    private readonly SemaphoreSlim _concurrency = new(4, 4);
    private readonly Dictionary<string, Task<string?>> _inFlight = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public ThumbnailCache(IHttpClientFactory httpClientFactory, AppPaths paths, ILogger<ThumbnailCache> logger)
    {
        _httpClientFactory = httpClientFactory;
        _directory = paths.ThumbnailCacheDirectory;
        _logger = logger;
    }

    public Task<string?> GetFileAsync(string? url, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
        {
            return Task.FromResult<string?>(null);
        }

        var path = PathFor(uri);
        try
        {
            var info = new FileInfo(path);
            if (info.Exists && info.Length > 0 && info.LastWriteTimeUtc > DateTime.UtcNow - TimeToLive)
            {
                return Task.FromResult<string?>(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Thumbnail cache lookup failed for {Url}", url);
        }

        lock (_lock)
        {
            if (_inFlight.TryGetValue(path, out var pending))
            {
                return pending;
            }

            var task = DownloadAsync(uri, path, cancellationToken);
            _inFlight[path] = task;
            _ = task.ContinueWith(
                _ =>
                {
                    lock (_lock)
                    {
                        _inFlight.Remove(path);
                    }
                },
                TaskScheduler.Default);
            return task;
        }
    }

    private async Task<string?> DownloadAsync(Uri uri, string path, CancellationToken cancellationToken)
    {
        await _concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_directory);
            // The download client carries no API key: thumbnails come from a public CDN.
            var client = _httpClientFactory.CreateClient(NexusHttpClients.Download);
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Thumbnail {Url} returned {Status}", uri, (int)response.StatusCode);
                return File.Exists(path) ? path : null;
            }

            if (response.Content.Headers.ContentLength > MaxBytes)
            {
                _logger.LogDebug("Thumbnail {Url} is too large ({Bytes} bytes)", uri, response.Content.Headers.ContentLength);
                return null;
            }

            var temp = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var target = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
            {
                await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temp, path, overwrite: true);
            return path;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Thumbnail {Url} could not be downloaded", uri);
            return File.Exists(path) ? path : null;
        }
        finally
        {
            _concurrency.Release();
        }
    }

    private string PathFor(Uri uri)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri))).ToLowerInvariant();
        var extension = Path.GetExtension(uri.AbsolutePath);
        if (extension.Length is 0 or > 5)
        {
            extension = ".img";
        }

        return Path.Combine(_directory, hash + extension.ToLowerInvariant());
    }
}
