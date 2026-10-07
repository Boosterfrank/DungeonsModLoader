using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace DungeonsModLoader.Core.AppUpdates;

/// <summary>Reads the latest release of a GitHub repository (public API, no token).</summary>
public interface IGitHubReleaseClient
{
    /// <summary>The latest non-draft release, or null when the repository has none yet. Throws <see cref="AppUpdateException"/> when GitHub cannot be reached.</summary>
    Task<GitHubRelease?> GetLatestReleaseAsync(string repository, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IGitHubReleaseClient"/>
public sealed class GitHubReleaseClient : IGitHubReleaseClient
{
    public const string ApiBaseUrl = "https://api.github.com/";

    private readonly Func<HttpClient> _clientFactory;
    private readonly ILogger<GitHubReleaseClient> _logger;

    /// <param name="clientFactory">Supplies an <see cref="HttpClient"/> that already carries the app's User-Agent (GitHub requires one).</param>
    public GitHubReleaseClient(Func<HttpClient> clientFactory, ILogger<GitHubReleaseClient> logger)
    {
        _clientFactory = clientFactory;
        _logger = logger;
    }

    public async Task<GitHubRelease?> GetLatestReleaseAsync(string repository, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiBaseUrl}repos/{repository}/releases/latest");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");

        HttpResponseMessage response;
        try
        {
            var client = _clientFactory();
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "GitHub release check failed to connect");
            throw new AppUpdateException("GitHub could not be reached. Check your internet connection and try again later.", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AppUpdateException("GitHub did not answer in time. Try again later.", ex);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                _logger.LogDebug("No releases published yet for {Repository}", repository);
                return null;
            }

            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                throw new AppUpdateException("GitHub is limiting requests from this PC right now. Try again in an hour.");
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("GitHub answered {Status} for the latest release of {Repository}", (int)response.StatusCode, repository);
                throw new AppUpdateException($"GitHub answered with an error ({(int)response.StatusCode}). Try again later.");
            }

            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                return Parse(document.RootElement);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "GitHub release answer could not be read");
                throw new AppUpdateException("GitHub sent an answer the app could not read. Try again later.", ex);
            }
        }
    }

    /// <summary>Maps the release JSON (<c>/releases/latest</c>) to <see cref="GitHubRelease"/>; tolerant of missing fields.</summary>
    public static GitHubRelease Parse(JsonElement root)
    {
        var assets = new List<GitHubReleaseAsset>();
        if (root.TryGetProperty("assets", out var assetsElement) && assetsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assetsElement.EnumerateArray())
            {
                var name = GetString(asset, "name");
                var url = GetString(asset, "browser_download_url");
                if (string.IsNullOrWhiteSpace(name) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
                {
                    continue;
                }

                var size = asset.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var bytes) ? bytes : 0;
                assets.Add(new GitHubReleaseAsset(name, uri, size));
            }
        }

        var htmlUrl = Uri.TryCreate(GetString(root, "html_url"), UriKind.Absolute, out var page) ? page : new Uri("https://github.com/" + AppInfo.GitHubRepository + "/releases");
        DateTimeOffset? published = root.TryGetProperty("published_at", out var publishedElement) && publishedElement.ValueKind == JsonValueKind.String && publishedElement.TryGetDateTimeOffset(out var at) ? at : null;

        return new GitHubRelease(
            GetString(root, "tag_name") ?? string.Empty,
            GetString(root, "name"),
            htmlUrl,
            GetBool(root, "prerelease"),
            GetBool(root, "draft"),
            published,
            GetString(root, "body"),
            assets);
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool GetBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
