using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using DungeonsModLoader.Core.Install;
using DungeonsModLoader.Nexus.Api;
using Microsoft.Extensions.Logging;

namespace DungeonsModLoader.Nexus.Download;

/// <inheritdoc cref="IDownloadService"/>
public sealed class HttpDownloadService : IDownloadService
{
    private const string PartialSuffix = ".part";
    private const int MaxAttempts = 4;
    private const int BufferSize = 128 * 1024;
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(150);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<HttpDownloadService> _logger;

    public HttpDownloadService(IHttpClientFactory httpClientFactory, ILogger<HttpDownloadService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public string FileNameFor(Uri url, string? fallback = null)
    {
        ArgumentNullException.ThrowIfNull(url);
        var last = url.AbsolutePath.TrimEnd('/').Split('/').LastOrDefault() ?? string.Empty;
        var decoded = Uri.UnescapeDataString(last);
        var name = FolderNameSanitizer.Sanitize(decoded, fallback ?? "download");
        if (string.IsNullOrEmpty(Path.GetExtension(name)) && !string.IsNullOrEmpty(fallback) && !string.IsNullOrEmpty(Path.GetExtension(fallback)))
        {
            name = FolderNameSanitizer.Sanitize(fallback, "download");
        }

        return name;
    }

    public async Task<string> DownloadAsync(Uri url, string destinationDirectory, string? fileName = null, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        if (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp)
        {
            throw new NexusException("The download location is not a web address.");
        }

        Directory.CreateDirectory(destinationDirectory);
        var finalName = string.IsNullOrWhiteSpace(fileName) ? FileNameFor(url) : FolderNameSanitizer.Sanitize(fileName, "download");
        var finalPath = Path.Combine(destinationDirectory, finalName);
        var partialPath = finalPath + PartialSuffix;
        var client = _httpClientFactory.CreateClient(NexusHttpClients.Download);

        Exception? lastError = null;
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await DownloadOnceAsync(client, url, partialPath, progress, cancellationToken).ConfigureAwait(false);
                File.Move(partialPath, finalPath, overwrite: true);
                _logger.LogInformation("Downloaded {File} ({Bytes} bytes) in {Attempts} attempt(s)", finalName, new FileInfo(finalPath).Length, attempt);
                return finalPath;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation("Download of {File} cancelled; the partial file is kept for resume", finalName);
                throw;
            }
            catch (NexusException)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
            {
                lastError = ex;
                _logger.LogWarning(ex, "Download of {File} failed (attempt {Attempt} of {Max})", finalName, attempt, MaxAttempts);
                if (attempt < MaxAttempts)
                {
                    progress?.Report(new DownloadProgress(PartialLength(partialPath), null, 0));
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), cancellationToken).ConfigureAwait(false);
                }
            }
        }

        throw new NexusUnavailableException("The download kept failing. Check your internet connection and try again; the app will resume where it stopped.", lastError);
    }

    private async Task DownloadOnceAsync(HttpClient client, Uri url, string partialPath, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        var existing = PartialLength(partialPath);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (existing > 0)
        {
            request.Headers.Range = new RangeHeaderValue(existing, null);
        }

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // The partial file is already complete (or the server disagrees about its size): start over to be safe.
            File.Delete(partialPath);
            existing = 0;
            throw new IOException("Range not satisfiable; restarting the download.");
        }

        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Gone)
        {
            throw new NexusLinkExpiredException("The download link has expired. Start the download again from the mod page.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new NexusApiException(response.StatusCode, $"The download server answered with an error ({(int)response.StatusCode}).");
        }

        var resuming = existing > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (existing > 0 && !resuming)
        {
            _logger.LogDebug("Server did not honour the range request; restarting {File}", partialPath);
            existing = 0;
        }

        long? total = response.Content.Headers.ContentLength is { } length ? length + (resuming ? existing : 0) : null;
        if (resuming && response.Content.Headers.ContentRange?.Length is { } rangeTotal)
        {
            total = rangeTotal;
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var target = new FileStream(partialPath, resuming ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);

        var received = existing;
        var buffer = new byte[BufferSize];
        var stopwatch = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;
        var windowStartBytes = received;
        var windowStart = TimeSpan.Zero;
        progress?.Report(new DownloadProgress(received, total, 0));

        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            received += read;

            var now = stopwatch.Elapsed;
            if (now - lastReport >= ProgressInterval)
            {
                var seconds = (now - windowStart).TotalSeconds;
                var speed = seconds > 0 ? (received - windowStartBytes) / seconds : 0;
                progress?.Report(new DownloadProgress(received, total, speed));
                lastReport = now;
                if (now - windowStart > TimeSpan.FromSeconds(3))
                {
                    windowStart = now;
                    windowStartBytes = received;
                }
            }
        }

        await target.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (total is { } expected && received != expected)
        {
            throw new IOException($"The download stopped early ({received} of {expected} bytes).");
        }

        progress?.Report(new DownloadProgress(received, total ?? received, 0));
    }

    private static long PartialLength(string partialPath)
    {
        try
        {
            return File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }
}
