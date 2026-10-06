namespace DungeonsModLoader.Nexus.Download;

/// <summary>Progress of a file download.</summary>
public sealed record DownloadProgress(long BytesReceived, long? TotalBytes, double BytesPerSecond)
{
    public double? Fraction => TotalBytes is > 0 ? Math.Clamp(BytesReceived / (double)TotalBytes.Value, 0, 1) : null;
}

/// <summary>Downloads files to the app's <c>downloads\</c> folder with progress, cancel, resume and retry.</summary>
public interface IDownloadService
{
    /// <summary>
    /// Downloads <paramref name="url"/> into <paramref name="destinationDirectory"/> and returns the final path.
    /// <paramref name="fileName"/> defaults to the name in the URL. A partial file from an earlier attempt is
    /// resumed with a Range request when the server supports it; transient network errors are retried.
    /// </summary>
    Task<string> DownloadAsync(Uri url, string destinationDirectory, string? fileName = null, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>The file name a URL would be saved under (sanitized last path segment).</summary>
    string FileNameFor(Uri url, string? fallback = null);
}
