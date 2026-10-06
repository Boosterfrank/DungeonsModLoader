using System.Net;
using System.Net.Http.Headers;
using System.Text;
using DungeonsModLoader.Nexus;
using DungeonsModLoader.Nexus.Api;

namespace DungeonsModLoader.Core.Tests.Nexus;

/// <summary>Scripted HTTP for the Nexus client tests: every request is recorded and answered by the handler function.</summary>
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, string?, Task<HttpResponseMessage>> _respond;

    public FakeHttpHandler(Func<HttpRequestMessage, string?, Task<HttpResponseMessage>> respond)
    {
        _respond = respond;
    }

    public List<(HttpRequestMessage Request, string? Body)> Requests { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request, body));
        return await _respond(request, body);
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK, Action<HttpResponseHeaders>? headers = null)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        headers?.Invoke(response.Headers);
        return response;
    }
}

internal sealed class FakeHttpClientFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler _handler;

    public FakeHttpClientFactory(HttpMessageHandler handler)
    {
        _handler = handler;
    }

    public HttpClient CreateClient(string name)
    {
        var client = new HttpClient(_handler, disposeHandler: false);
        if (name == NexusHttpClients.Api)
        {
            client.BaseAddress = new Uri(NexusConstants.ApiBaseUrl);
        }

        client.DefaultRequestHeaders.TryAddWithoutValidation("Application-Name", NexusConstants.ApplicationName);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Application-Version", "0.0.1");
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "DungeonsModLoader/0.0.1 (test)");
        return client;
    }
}

internal sealed class FakeKeyAccessor : INexusApiKeyAccessor
{
    public string? ApiKey { get; set; }
}

/// <summary>In-memory cache so tests do not touch the disk.</summary>
internal sealed class MemoryCache : INexusCache
{
    private readonly Dictionary<string, (object Value, DateTimeOffset ExpiresAt, DateTimeOffset StoredAt)> _items = new();

    public Task<CacheHit<T>?> GetAsync<T>(string key, bool allowExpired = false, CancellationToken cancellationToken = default)
    {
        if (_items.TryGetValue(key, out var item) && item.Value is T value && (allowExpired || item.ExpiresAt > DateTimeOffset.UtcNow))
        {
            return Task.FromResult<CacheHit<T>?>(new CacheHit<T>(value, item.ExpiresAt, item.StoredAt));
        }

        return Task.FromResult<CacheHit<T>?>(null);
    }

    public Task SetAsync<T>(string key, T value, TimeSpan timeToLive, CancellationToken cancellationToken = default)
    {
        _items[key] = (value!, DateTimeOffset.UtcNow + timeToLive, DateTimeOffset.UtcNow);
        return Task.CompletedTask;
    }

    public void Expire(string key)
    {
        if (_items.TryGetValue(key, out var item))
        {
            _items[key] = (item.Value, DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1), item.StoredAt);
        }
    }

    public IEnumerable<string> Keys => _items.Keys;

    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        _items.Remove(key);
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        _items.Clear();
        return Task.CompletedTask;
    }
}
