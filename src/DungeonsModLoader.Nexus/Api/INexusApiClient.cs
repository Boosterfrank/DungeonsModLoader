namespace DungeonsModLoader.Nexus.Api;

/// <summary>Names of the HttpClients the Nexus layer registers.</summary>
public static class NexusHttpClients
{
    /// <summary>API requests: base address api.nexusmods.com, identification headers, 30 s timeout. The API key is added per request.</summary>
    public const string Api = "NexusApi";

    /// <summary>File and image downloads from the CDN: identification headers only (never the API key), no timeout.</summary>
    public const string Download = "NexusDownload";
}

/// <summary>Where the client takes the current API key from (owned by the session).</summary>
public interface INexusApiKeyAccessor
{
    string? ApiKey { get; }
}

/// <summary>
/// Nexus Mods API: v1 REST for account, lists, mod info, files, download links and update detection; v2 GraphQL
/// for text search, requirements and for browsing without an API key. Responses are cached on disk with short
/// TTLs; stale cache entries are served when Nexus is unreachable. All methods throw <see cref="NexusException"/>
/// subclasses with user-facing messages.
/// </summary>
public interface INexusApiClient
{
    /// <summary>The rate-limit headers of the most recent v1 response, or null before the first one.</summary>
    NexusRateLimit? RateLimit { get; }

    event EventHandler<NexusRateLimit>? RateLimitChanged;

    /// <summary>True when an API key is available, so the v1 routes can be used.</summary>
    bool HasApiKey { get; }

    /// <summary>Checks a key and returns the account behind it (<c>/v1/users/validate.json</c>; not rate limited).</summary>
    Task<NexusUser> ValidateAsync(string apiKey, CancellationToken cancellationToken = default);

    /// <summary>Trending / latest added / recently updated. v1 (10 items, no paging) with a key, GraphQL (pageable) without.</summary>
    Task<NexusModPage> GetListAsync(NexusListKind kind, int offset = 0, int count = 20, bool refresh = false, CancellationToken cancellationToken = default);

    /// <summary>Text search via GraphQL (works without a key). Empty text lists everything sorted by endorsements.</summary>
    Task<NexusModPage> SearchAsync(string query, int offset = 0, int count = 20, bool refresh = false, CancellationToken cancellationToken = default);

    /// <summary>Full mod info including the BBCode description.</summary>
    Task<NexusMod> GetModAsync(long modId, bool refresh = false, CancellationToken cancellationToken = default);

    /// <summary>The mod's files. With a key via v1 (includes file-update chains), otherwise via GraphQL.</summary>
    Task<NexusFileList> GetFilesAsync(long modId, bool refresh = false, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<NexusRequirement>> GetRequirementsAsync(long modId, bool refresh = false, CancellationToken cancellationToken = default);

    /// <summary>Mods changed in the period (v1, needs a key). Cached for 5 minutes.</summary>
    Task<IReadOnlyList<NexusUpdatedMod>> GetUpdatedAsync(NexusUpdatePeriod period, bool refresh = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Download locations for a file (v1, needs a key, never cached). Free accounts must pass the <c>key</c> and
    /// <c>expires</c> of an <c>nxm://</c> link; without them the call fails with <see cref="NexusPremiumRequiredException"/>.
    /// </summary>
    Task<IReadOnlyList<NexusDownloadLink>> GetDownloadLinksAsync(long modId, long fileId, string? nxmKey = null, long? nxmExpires = null, CancellationToken cancellationToken = default);
}
