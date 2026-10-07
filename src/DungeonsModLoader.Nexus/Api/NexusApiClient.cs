using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace DungeonsModLoader.Nexus.Api;

/// <inheritdoc cref="INexusApiClient"/>
public sealed class NexusApiClient : INexusApiClient
{
    private static readonly TimeSpan ListTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SearchTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ModTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan FilesTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RequirementsTtl = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan UpdatedTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RetryDelayAfter429 = TimeSpan.FromSeconds(1);
    private const int MaxRateLimitRetries = 2;

    private const string ListFields =
        "modId uid name summary author version endorsements downloads pictureUrl thumbnailUrl createdAt updatedAt status adultContent directDownloadEnabled uploader { name memberId } modCategory { categoryId name }";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly INexusApiKeyAccessor _apiKey;
    private readonly INexusCache _cache;
    private readonly ILogger<NexusApiClient> _logger;
    private NexusRateLimit? _rateLimit;

    public NexusApiClient(IHttpClientFactory httpClientFactory, INexusApiKeyAccessor apiKey, INexusCache cache, ILogger<NexusApiClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _apiKey = apiKey;
        _cache = cache;
        _logger = logger;
    }

    public NexusRateLimit? RateLimit => _rateLimit;

    public event EventHandler<NexusRateLimit>? RateLimitChanged;

    public bool HasApiKey => !string.IsNullOrWhiteSpace(_apiKey.ApiKey);

    // ---------------------------------------------------------------------------------------------------------------
    // Account
    // ---------------------------------------------------------------------------------------------------------------

    public async Task<NexusUser> ValidateAsync(string apiKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        using var document = await GetV1Async<JsonDocument>("v1/users/validate.json", apiKey, cancellationToken).ConfigureAwait(false);
        var user = V1Validate.Map(document.RootElement);
        _logger.LogInformation("API key validated for Nexus user '{User}' (premium: {Premium})", user.Name, user.IsPremium);
        return user;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Lists & search
    // ---------------------------------------------------------------------------------------------------------------

    public Task<NexusModPage> GetListAsync(NexusListKind kind, int offset = 0, int count = 20, bool refresh = false, CancellationToken cancellationToken = default)
    {
        count = Math.Clamp(count, 1, 50);
        offset = Math.Max(0, offset);

        if (HasApiKey)
        {
            if (offset > 0)
            {
                // The v1 lists are fixed at 10 items.
                return Task.FromResult(new NexusModPage(Array.Empty<NexusMod>(), offset, offset));
            }

            var route = kind switch
            {
                NexusListKind.Trending => "trending",
                NexusListKind.LatestAdded => "latest_added",
                _ => "latest_updated",
            };
            return CachedAsync(
                $"v1:list:{route}",
                ListTtl,
                refresh,
                async ct =>
                {
                    var mods = await GetV1Async<List<V1ModInfo>>($"v1/games/{NexusConstants.GameDomain}/mods/{route}.json", _apiKey.ApiKey, ct).ConfigureAwait(false);
                    var items = mods.Where(m => m is not null).Select(m => m.Map()).ToList();
                    if (kind == NexusListKind.RecentlyUpdated)
                    {
                        // The route returns ascending by update time.
                        items = items.OrderByDescending(m => m.UpdatedAt).ToList();
                    }

                    return new NexusModPage(items, items.Count, 0);
                },
                cancellationToken);
        }

        var sort = kind switch
        {
            NexusListKind.Trending => "{ \"downloads\": { \"direction\": \"DESC\" } }",
            NexusListKind.LatestAdded => "{ \"createdAt\": { \"direction\": \"DESC\" } }",
            _ => "{ \"updatedAt\": { \"direction\": \"DESC\" } }",
        };
        return CachedAsync(
            $"v2:list:{kind}:{offset}:{count}",
            ListTtl,
            refresh,
            ct => QueryModsAsync(null, sort, offset, count, ct),
            cancellationToken);
    }

    public Task<NexusModPage> SearchAsync(string query, int offset = 0, int count = 20, bool refresh = false, CancellationToken cancellationToken = default)
    {
        var text = (query ?? string.Empty).Trim();
        count = Math.Clamp(count, 1, 50);
        offset = Math.Max(0, offset);
        var sort = text.Length == 0
            ? "{ \"endorsements\": { \"direction\": \"DESC\" } }"
            : "{ \"relevance\": { \"direction\": \"DESC\" } }";
        return CachedAsync(
            $"v2:search:{text.ToLowerInvariant()}:{offset}:{count}",
            SearchTtl,
            refresh,
            ct => QueryModsAsync(text.Length == 0 ? null : text, sort, offset, count, ct),
            cancellationToken);
    }

    private async Task<NexusModPage> QueryModsAsync(string? nameQuery, string sortJson, int offset, int count, CancellationToken cancellationToken)
    {
        const string query = "query($filter: ModsFilter, $sort: [ModsSort!], $count: Int, $offset: Int) { mods(filter: $filter, sort: $sort, count: $count, offset: $offset) { totalCount nodes { " + ListFields + " } } }";

        var filters = new List<object>
        {
            new { gameDomainName = new[] { new { value = NexusConstants.GameDomain, op = "EQUALS" } } },
        };
        if (nameQuery is not null)
        {
            // WILDCARD matches every term anywhere in the name; relevance sorting ranks the best hits first.
            filters.Add(new { name = new[] { new { value = nameQuery, op = "WILDCARD" } } });
        }

        var variables = new Dictionary<string, object?>
        {
            ["filter"] = new { op = "AND", filter = filters },
            ["sort"] = new[] { JsonSerializer.Deserialize<JsonElement>(sortJson) },
            ["count"] = count,
            ["offset"] = offset,
        };

        var data = await PostGraphQlAsync<GqlModsData>(query, variables, cancellationToken).ConfigureAwait(false);
        var page = data.Mods;
        if (page is null)
        {
            return new NexusModPage(Array.Empty<NexusMod>(), 0, offset);
        }

        var items = page.Nodes.Where(n => n is not null).Select(n => n.Map()).ToList();
        return new NexusModPage(items, Math.Max(page.TotalCount, offset + items.Count), offset);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Mod detail, files, requirements
    // ---------------------------------------------------------------------------------------------------------------

    public Task<NexusMod> GetModAsync(long modId, bool refresh = false, CancellationToken cancellationToken = default)
    {
        if (HasApiKey)
        {
            return CachedAsync(
                $"v1:mod:{modId}",
                ModTtl,
                refresh,
                async ct =>
                {
                    var info = await GetV1Async<V1ModInfo>($"v1/games/{NexusConstants.GameDomain}/mods/{modId}.json", _apiKey.ApiKey, ct).ConfigureAwait(false);
                    return info.Map();
                },
                cancellationToken);
        }

        return CachedAsync(
            $"v2:mod:{modId}",
            ModTtl,
            refresh,
            async ct =>
            {
                const string query = "query($m: ID!, $g: ID!) { mod(modId: $m, gameId: $g) { " + ListFields + " description } }";
                var data = await PostGraphQlAsync<GqlModData>(query, IdVariables(modId), ct).ConfigureAwait(false);
                return data.Mod?.Map() ?? throw new NexusApiException(HttpStatusCode.NotFound, "This mod does not exist on Nexus Mods (it may have been removed).");
            },
            cancellationToken);
    }

    public Task<NexusFileList> GetFilesAsync(long modId, bool refresh = false, CancellationToken cancellationToken = default)
    {
        if (HasApiKey)
        {
            return CachedAsync(
                $"v1:files:{modId}",
                FilesTtl,
                refresh,
                async ct =>
                {
                    var list = await GetV1Async<V1FileList>($"v1/games/{NexusConstants.GameDomain}/mods/{modId}/files.json", _apiKey.ApiKey, ct).ConfigureAwait(false);
                    return list.Map();
                },
                cancellationToken);
        }

        return CachedAsync(
            $"v2:files:{modId}",
            FilesTtl,
            refresh,
            async ct =>
            {
                const string query = "query($m: ID!, $g: ID!) { modFiles(modId: $m, gameId: $g) { fileId name version category categoryId size sizeInBytes date description primary changelogText } }";
                var data = await PostGraphQlAsync<GqlModData>(query, IdVariables(modId), ct).ConfigureAwait(false);
                var files = (data.ModFiles ?? new List<GqlModFile>()).Where(f => f is not null).Select(f => f.Map()).ToList();
                return new NexusFileList(files, Array.Empty<NexusFileUpdate>());
            },
            cancellationToken);
    }

    public Task<IReadOnlyList<NexusRequirement>> GetRequirementsAsync(long modId, bool refresh = false, CancellationToken cancellationToken = default)
    {
        return CachedAsync<IReadOnlyList<NexusRequirement>>(
            $"v2:requirements:{modId}",
            RequirementsTtl,
            refresh,
            async ct =>
            {
                const string query = "query($m: ID!, $g: ID!) { mod(modId: $m, gameId: $g) { modId legacyModRequirementsEnabled modRequirements(skipDisabledRequirements: true) { nexusRequirements(count: 25) { totalCount nodes { id gameId modId modName notes url externalRequirement } } } } }";
                var data = await PostGraphQlAsync<GqlModData>(query, IdVariables(modId), ct).ConfigureAwait(false);
                var nodes = data.Mod?.ModRequirements?.NexusRequirements?.Nodes ?? new List<GqlRequirement>();
                return nodes.Where(n => n is not null).Select(n => n.Map()).ToList();
            },
            cancellationToken);
    }

    private static Dictionary<string, object?> IdVariables(long modId) => new()
    {
        ["m"] = modId.ToString(CultureInfo.InvariantCulture),
        ["g"] = NexusConstants.GameId.ToString(CultureInfo.InvariantCulture),
    };

    // ---------------------------------------------------------------------------------------------------------------
    // Updates & downloads (v1 only)
    // ---------------------------------------------------------------------------------------------------------------

    public Task<IReadOnlyList<NexusUpdatedMod>> GetUpdatedAsync(NexusUpdatePeriod period, bool refresh = false, CancellationToken cancellationToken = default)
    {
        var periodText = period switch
        {
            NexusUpdatePeriod.Day => "1d",
            NexusUpdatePeriod.Week => "1w",
            _ => "1m",
        };
        return CachedAsync<IReadOnlyList<NexusUpdatedMod>>(
            $"v1:updated:{periodText}",
            UpdatedTtl,
            refresh,
            async ct =>
            {
                var list = await GetV1Async<List<V1UpdatedMod>>($"v1/games/{NexusConstants.GameDomain}/mods/updated.json?period={periodText}", RequireApiKey(), ct).ConfigureAwait(false);
                return list.Where(u => u is not null).Select(u => u.Map()).ToList();
            },
            cancellationToken);
    }

    public async Task<IReadOnlyList<NexusDownloadLink>> GetDownloadLinksAsync(long modId, long fileId, string? nxmKey = null, long? nxmExpires = null, CancellationToken cancellationToken = default)
    {
        var route = $"v1/games/{NexusConstants.GameDomain}/mods/{modId}/files/{fileId}/download_link.json";
        if (!string.IsNullOrEmpty(nxmKey) && nxmExpires is not null)
        {
            route += $"?key={Uri.EscapeDataString(nxmKey)}&expires={nxmExpires.Value.ToString(CultureInfo.InvariantCulture)}";
        }

        try
        {
            var links = await GetV1Async<List<V1DownloadLink>>(route, RequireApiKey(), cancellationToken).ConfigureAwait(false);
            var mapped = links.Where(l => l is not null).Select(l => l.Map()).Where(l => l is not null).Select(l => l!).ToList();
            if (mapped.Count == 0)
            {
                throw new NexusApiException(HttpStatusCode.OK, "Nexus Mods returned no download location for this file. Try again later.");
            }

            _logger.LogInformation("Download links for mod {Mod} file {File}: {Count} location(s), first '{Name}'", modId, fileId, mapped.Count, mapped[0].ShortName);
            return mapped;
        }
        catch (NexusApiException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new NexusPremiumRequiredException();
        }
        catch (NexusApiException ex) when (ex.StatusCode == HttpStatusCode.Gone)
        {
            throw new NexusLinkExpiredException("This download link has expired. Open the mod page again and click \"Mod Manager Download\" once more.");
        }
        catch (NexusApiException ex) when (ex.StatusCode == HttpStatusCode.BadRequest && nxmKey is not null)
        {
            throw new NexusLinkExpiredException("This download link was not made for your account. Make sure you are logged into nexusmods.com with the same account as in the app, then click \"Mod Manager Download\" again.");
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Transport
    // ---------------------------------------------------------------------------------------------------------------

    private string RequireApiKey() =>
        _apiKey.ApiKey is { Length: > 0 } key ? key : throw new NexusAuthException(HttpStatusCode.Unauthorized, "Add your Nexus Mods API key in Settings first.");

    private async Task<T> CachedAsync<T>(string key, TimeSpan ttl, bool refresh, Func<CancellationToken, Task<T>> fetch, CancellationToken cancellationToken)
    {
        if (!refresh)
        {
            var hit = await _cache.GetAsync<T>(key, allowExpired: false, cancellationToken).ConfigureAwait(false);
            if (hit is not null)
            {
                return hit.Value;
            }
        }

        try
        {
            var value = await fetch(cancellationToken).ConfigureAwait(false);
            await _cache.SetAsync(key, value, ttl, cancellationToken).ConfigureAwait(false);
            return value;
        }
        catch (Exception ex) when (ex is NexusUnavailableException or NexusRateLimitException)
        {
            // Offline or throttled: an older answer beats no answer.
            var stale = await _cache.GetAsync<T>(key, allowExpired: true, CancellationToken.None).ConfigureAwait(false);
            if (stale is not null)
            {
                _logger.LogInformation("Serving cached Nexus data for {Key} from {StoredAt:u} because the live request failed: {Reason}", key, stale.StoredAt, ex.Message);
                return stale.Value;
            }

            throw;
        }
    }

    /// <summary>GET on a v1 route with the identification headers and the given API key; retries briefly on 429.</summary>
    private async Task<T> GetV1Async<T>(string route, string? apiKey, CancellationToken cancellationToken)
    {
        ThrowIfKnownExhausted();
        var client = _httpClientFactory.CreateClient(NexusHttpClients.Api);
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, route);
            if (!string.IsNullOrEmpty(apiKey))
            {
                request.Headers.TryAddWithoutValidation("apikey", apiKey);
            }

            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "Nexus request {Route} failed: {Message}", SecretMasker.Mask(route), ex.Message);
                throw new NexusUnavailableException("Nexus Mods could not be reached. Check your internet connection and try again.", ex);
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("Nexus request {Route} timed out", SecretMasker.Mask(route));
                throw new NexusUnavailableException("Nexus Mods took too long to answer. Try again in a moment.", ex);
            }

            using (response)
            {
                TrackRateLimit(response);
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    var limit = _rateLimit;
                    var resumeAt = ReadRetryAfter(response) ?? limit?.ResumeAt;
                    if (attempt < MaxRateLimitRetries && (limit is null || !limit.IsExhausted))
                    {
                        // A burst limit (nginx, 30 requests per second), not the hourly budget: wait a second and retry.
                        _logger.LogInformation("Nexus answered 429 for {Route}; retrying after {Delay}", SecretMasker.Mask(route), RetryDelayAfter429);
                        await Task.Delay(RetryDelayAfter429, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    _logger.LogWarning("Nexus rate limit reached for {Route}; resume at {ResumeAt}", SecretMasker.Mask(route), resumeAt);
                    throw new NexusRateLimitException(resumeAt, limit);
                }

                if (!response.IsSuccessStatusCode)
                {
                    var serverMessage = await ReadErrorMessageAsync(response, cancellationToken).ConfigureAwait(false);
                    _logger.LogWarning("Nexus answered {Status} for {Route}: {Message}", (int)response.StatusCode, SecretMasker.Mask(route), serverMessage);
                    throw MapError(response.StatusCode, serverMessage);
                }

                try
                {
                    var value = await response.Content.ReadFromJsonAsync<T>(JsonSettings.V1, cancellationToken).ConfigureAwait(false);
                    return value ?? throw new NexusApiException(response.StatusCode, "Nexus Mods returned an empty answer.");
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Nexus answer for {Route} could not be read", SecretMasker.Mask(route));
                    throw new NexusApiException(response.StatusCode, "Nexus Mods returned an answer the app could not read.", null, ex);
                }
            }
        }
    }

    private async Task<T> PostGraphQlAsync<T>(string query, Dictionary<string, object?> variables, CancellationToken cancellationToken)
        where T : class
    {
        var client = _httpClientFactory.CreateClient(NexusHttpClients.Api);
        using var request = new HttpRequestMessage(HttpMethod.Post, NexusConstants.GraphQlUrl)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { query, variables }, JsonSettings.V2), Encoding.UTF8, "application/json"),
        };
        if (_apiKey.ApiKey is { Length: > 0 } key)
        {
            request.Headers.TryAddWithoutValidation("apikey", key);
        }

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Nexus GraphQL request failed: {Message}", ex.Message);
            throw new NexusUnavailableException("Nexus Mods could not be reached. Check your internet connection and try again.", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Nexus GraphQL request timed out");
            throw new NexusUnavailableException("Nexus Mods took too long to answer. Try again in a moment.", ex);
        }

        using (response)
        {
            TrackRateLimit(response);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throw new NexusRateLimitException(ReadRetryAfter(response) ?? _rateLimit?.ResumeAt, _rateLimit);
            }

            if (!response.IsSuccessStatusCode)
            {
                var serverMessage = await ReadErrorMessageAsync(response, cancellationToken).ConfigureAwait(false);
                _logger.LogWarning("Nexus GraphQL answered {Status}: {Message}", (int)response.StatusCode, serverMessage);
                throw MapError(response.StatusCode, serverMessage);
            }

            GqlResponse<T>? envelope;
            try
            {
                envelope = await response.Content.ReadFromJsonAsync<GqlResponse<T>>(JsonSettings.V2, cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Nexus GraphQL answer could not be read");
                throw new NexusApiException(response.StatusCode, "Nexus Mods returned an answer the app could not read.", null, ex);
            }

            if (envelope?.Data is null)
            {
                var message = envelope?.Errors?.FirstOrDefault()?.Message;
                _logger.LogWarning("Nexus GraphQL returned no data: {Message}", message ?? "(no error message)");
                throw new NexusApiException(response.StatusCode, "Nexus Mods could not answer this request right now.", message);
            }

            if (envelope.Errors is { Count: > 0 })
            {
                _logger.LogDebug("Nexus GraphQL returned data with {Count} error(s); first: {Message}", envelope.Errors.Count, envelope.Errors[0].Message);
            }

            return envelope.Data;
        }
    }

    private void ThrowIfKnownExhausted()
    {
        var limit = _rateLimit;
        if (limit is { IsExhausted: true } && limit.ResumeAt is { } resumeAt && resumeAt > DateTimeOffset.UtcNow)
        {
            throw new NexusRateLimitException(resumeAt, limit);
        }
    }

    private void TrackRateLimit(HttpResponseMessage response)
    {
        var limit = NexusRateLimit.FromHeaders(response.Headers);
        if (limit is null)
        {
            return;
        }

        _rateLimit = limit;
        _logger.LogDebug("Nexus rate limit: {HourlyRemaining}/{HourlyLimit} this hour, {DailyRemaining}/{DailyLimit} today", limit.HourlyRemaining, limit.HourlyLimit, limit.DailyRemaining, limit.DailyLimit);
        try
        {
            RateLimitChanged?.Invoke(this, limit);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A RateLimitChanged handler threw");
        }
    }

    private static DateTimeOffset? ReadRetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter is null)
        {
            return null;
        }

        if (retryAfter.Delta is { } delta)
        {
            return DateTimeOffset.UtcNow + delta;
        }

        return retryAfter.Date;
    }

    private static NexusApiException MapError(HttpStatusCode status, string? serverMessage) => status switch
    {
        HttpStatusCode.Unauthorized => new NexusAuthException(status, "Nexus Mods rejected the API key. Make sure you copied the whole key from the API keys page, or request a new key there (a revoked key stops working).", serverMessage),
        HttpStatusCode.Forbidden => new NexusApiException(status, "Nexus Mods refused this request for your account.", serverMessage),
        HttpStatusCode.NotFound => new NexusApiException(status, "Nexus Mods has no such mod or file (it may have been removed).", serverMessage),
        HttpStatusCode.Gone => new NexusApiException(status, "This link has expired.", serverMessage),
        >= HttpStatusCode.InternalServerError => new NexusApiException(status, "Nexus Mods is having trouble right now. Try again in a few minutes.", serverMessage),
        _ => new NexusApiException(status, $"Nexus Mods answered with an error ({(int)status}).", serverMessage),
    };

    private static async Task<string?> ReadErrorMessageAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            try
            {
                using var document = JsonDocument.Parse(text);
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var name in new[] { "message", "error_description", "error" })
                    {
                        if (document.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                        {
                            return value.GetString();
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // Plain text body.
            }

            return text.Length > 300 ? text[..300] : text;
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            return null;
        }
    }
}
