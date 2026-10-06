using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using DungeonsModLoader.Nexus.Api;
using Microsoft.Extensions.Logging;

namespace DungeonsModLoader.Nexus.Auth;

/// <summary>
/// "Log in with Nexus" over <c>wss://sso.nexusmods.com</c> (protocol 2): connect, send <c>{id, token, protocol}</c>,
/// receive a <c>connection_token</c>, open <c>https://www.nexusmods.com/sso?id=…&amp;application=&lt;slug&gt;</c>
/// in the browser, keep the socket alive (ping every 30 s) and reconnect with the token until the website delivers
/// the <c>api_key</c>. Only usable once Nexus has issued the app slug (<see cref="NexusConstants.AppSlug"/>).
/// </summary>
public sealed class SsoAuthProvider : INexusAuthProvider
{
    private const int MaxReconnects = 5;
    private static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan OverallTimeout = TimeSpan.FromMinutes(5);

    private readonly IUrlOpener _urlOpener;
    private readonly ILogger<SsoAuthProvider> _logger;
    private readonly Func<Uri, IWebSocketConnection> _connectionFactory;

    public SsoAuthProvider(IUrlOpener urlOpener, ILogger<SsoAuthProvider> logger)
        : this(urlOpener, logger, uri => new ClientWebSocketConnection(uri, KeepAlive))
    {
    }

    /// <summary>Test hook: supplies the websocket.</summary>
    internal SsoAuthProvider(IUrlOpener urlOpener, ILogger<SsoAuthProvider> logger, Func<Uri, IWebSocketConnection> connectionFactory)
    {
        _urlOpener = urlOpener;
        _logger = logger;
        _connectionFactory = connectionFactory;
    }

    public NexusAuthMethod Method => NexusAuthMethod.Sso;

    public bool IsAvailable => NexusConstants.IsSsoAvailable;

    public async Task<string> AcquireApiKeyAsync(string? userInput, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            throw new NexusException("Logging in with Nexus is not available in this build yet; use a personal API key instead.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(OverallTimeout);
        var token = timeout.Token;

        var connectionId = Guid.NewGuid();
        string? connectionToken = null;
        var browserOpened = false;
        var attempt = 0;

        while (true)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                progress?.Report(connectionToken is null ? "Connecting to Nexus Mods..." : "Reconnecting to Nexus Mods...");
                using var socket = _connectionFactory(new Uri(NexusConstants.SsoWebSocketUrl));
                await socket.ConnectAsync(token).ConfigureAwait(false);

                var hello = JsonSerializer.Serialize(new { id = connectionId.ToString("D"), token = connectionToken, protocol = 2 });
                await socket.SendTextAsync(hello, token).ConfigureAwait(false);

                while (true)
                {
                    var message = await socket.ReceiveTextAsync(token).ConfigureAwait(false);
                    if (message is null)
                    {
                        break; // closed by the server; reconnect below
                    }

                    var reply = Parse(message);
                    if (!reply.Success)
                    {
                        throw new NexusAuthException(System.Net.HttpStatusCode.Unauthorized, "Nexus Mods refused the login: " + (reply.Error ?? "unknown error"));
                    }

                    if (reply.ApiKey is not null)
                    {
                        _logger.LogInformation("SSO login completed ({Key})", SecretMasker.Describe(reply.ApiKey));
                        try
                        {
                            await socket.CloseAsync(CancellationToken.None).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException)
                        {
                            _logger.LogDebug(ex, "SSO socket close failed after the key arrived");
                        }

                        return reply.ApiKey;
                    }

                    if (reply.ConnectionToken is not null)
                    {
                        connectionToken = reply.ConnectionToken;
                        if (!browserOpened)
                        {
                            browserOpened = true;
                            var url = NexusConstants.SsoPageUrl(connectionId);
                            _logger.LogInformation("SSO connection established; opening the browser for approval");
                            _urlOpener.OpenUrl(url);
                            progress?.Report("Approve the login in your browser. Waiting for Nexus Mods...");
                        }

                        continue;
                    }

                    _logger.LogDebug("Ignoring unexpected SSO message");
                }
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new NexusException("The login was not approved in time. Try again and approve it in the browser within five minutes.");
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or HttpRequestException or JsonException)
            {
                attempt++;
                _logger.LogWarning(ex, "SSO connection problem (attempt {Attempt} of {Max})", attempt, MaxReconnects);
                if (attempt >= MaxReconnects)
                {
                    throw new NexusUnavailableException("The connection to Nexus Mods was lost. Check your internet connection and try again.", ex);
                }
            }

            await Task.Delay(ReconnectDelay, token).ConfigureAwait(false);
        }
    }

    internal static SsoReply Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var success = root.TryGetProperty("success", out var successValue) && successValue.ValueKind == JsonValueKind.True;
        string? error = root.TryGetProperty("error", out var errorValue) && errorValue.ValueKind == JsonValueKind.String ? errorValue.GetString() : null;
        string? apiKey = null;
        string? connectionToken = null;
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
        {
            if (data.TryGetProperty("api_key", out var key) && key.ValueKind == JsonValueKind.String)
            {
                apiKey = key.GetString();
            }

            if (data.TryGetProperty("connection_token", out var tokenValue) && tokenValue.ValueKind == JsonValueKind.String)
            {
                connectionToken = tokenValue.GetString();
            }
        }

        return new SsoReply(success, error, apiKey, connectionToken);
    }

    internal sealed record SsoReply(bool Success, string? Error, string? ApiKey, string? ConnectionToken);
}

/// <summary>Minimal websocket surface so the SSO flow can be tested without a server.</summary>
public interface IWebSocketConnection : IDisposable
{
    Task ConnectAsync(CancellationToken cancellationToken);

    Task SendTextAsync(string text, CancellationToken cancellationToken);

    /// <summary>The next text message, or null when the server closed the connection.</summary>
    Task<string?> ReceiveTextAsync(CancellationToken cancellationToken);

    Task CloseAsync(CancellationToken cancellationToken);
}

internal sealed class ClientWebSocketConnection : IWebSocketConnection
{
    private readonly ClientWebSocket _socket = new();
    private readonly Uri _uri;

    public ClientWebSocketConnection(Uri uri, TimeSpan keepAlive)
    {
        _uri = uri;
        _socket.Options.KeepAliveInterval = keepAlive;
    }

    public Task ConnectAsync(CancellationToken cancellationToken) => _socket.ConnectAsync(_uri, cancellationToken);

    public Task SendTextAsync(string text, CancellationToken cancellationToken) =>
        _socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, endOfMessage: true, cancellationToken);

    public async Task<string?> ReceiveTextAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[8 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await _socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(message.ToArray());
            }
        }
    }

    public Task CloseAsync(CancellationToken cancellationToken) =>
        _socket.State is WebSocketState.Open or WebSocketState.CloseReceived
            ? _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", cancellationToken)
            : Task.CompletedTask;

    public void Dispose() => _socket.Dispose();
}
