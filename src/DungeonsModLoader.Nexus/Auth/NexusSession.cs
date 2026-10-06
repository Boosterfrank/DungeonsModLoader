using DungeonsModLoader.Nexus.Api;
using Microsoft.Extensions.Logging;

namespace DungeonsModLoader.Nexus.Auth;

/// <summary>Holds the key for the API client; written only by <see cref="NexusSession"/>.</summary>
public sealed class NexusApiKeyHolder : INexusApiKeyAccessor
{
    private volatile string? _apiKey;

    public string? ApiKey => _apiKey;

    internal void Set(string? apiKey) => _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
}

/// <inheritdoc cref="INexusSession"/>
public sealed class NexusSession : INexusSession
{
    private readonly INexusApiClient _client;
    private readonly INexusApiKeyStore _store;
    private readonly NexusApiKeyHolder _holder;
    private readonly ILogger<NexusSession> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public NexusSession(INexusApiClient client, INexusApiKeyStore store, NexusApiKeyHolder holder, IEnumerable<INexusAuthProvider> providers, ILogger<NexusSession> logger)
    {
        _client = client;
        _store = store;
        _holder = holder;
        _logger = logger;
        Providers = providers.Where(p => p.IsAvailable).ToList();
    }

    public NexusSessionStatus Status { get; private set; } = NexusSessionStatus.LoggedOut;

    public NexusUser? User { get; private set; }

    public bool HasApiKey => _holder.ApiKey is not null;

    public bool IsPremium => User?.IsPremium == true;

    public string? LastError { get; private set; }

    public IReadOnlyList<INexusAuthProvider> Providers { get; }

    public event EventHandler? Changed;

    public async Task RestoreAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var key = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (key is null)
            {
                _holder.Set(null);
                SetState(NexusSessionStatus.LoggedOut, null, null);
                _logger.LogInformation("No Nexus API key stored; browsing works without one, downloads need one");
                return;
            }

            _holder.Set(key);
            await ValidateStoredKeyAsync(key, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        RaiseChanged();
    }

    public async Task<NexusUser> LoginAsync(INexusAuthProvider provider, string? userInput = null, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SetState(NexusSessionStatus.Verifying, User, null);
            RaiseChanged();

            string key;
            NexusUser user;
            try
            {
                key = await provider.AcquireApiKeyAsync(userInput, progress, cancellationToken).ConfigureAwait(false);
                progress?.Report("Checking the key with Nexus Mods...");
                user = await _client.ValidateAsync(key, cancellationToken).ConfigureAwait(false);
            }
            catch (NexusException ex)
            {
                // Back to whatever we had before (a previous key keeps working).
                var previous = _holder.ApiKey is null ? NexusSessionStatus.LoggedOut : (User is null ? NexusSessionStatus.Unverified : NexusSessionStatus.LoggedIn);
                SetState(previous, User, ex.Message);
                _logger.LogWarning("Nexus login via {Method} failed: {Message}", provider.Method, ex.Message);
                throw;
            }

            try
            {
                await _store.SaveAsync(key, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
            {
                _logger.LogError(ex, "The Nexus API key could not be stored");
                throw new NexusException("The key was accepted by Nexus Mods but could not be saved on this PC. Make sure the app data folder is writable.", ex);
            }

            _holder.Set(key);
            SetState(NexusSessionStatus.LoggedIn, user, null);
            _logger.LogInformation("Logged in to Nexus Mods as '{User}' via {Method} (premium: {Premium})", user.Name, provider.Method, user.IsPremium);
            return user;
        }
        finally
        {
            _gate.Release();
            RaiseChanged();
        }
    }

    public async Task<NexusUser?> RevalidateAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var key = _holder.ApiKey;
            if (key is null)
            {
                SetState(NexusSessionStatus.LoggedOut, null, null);
                return null;
            }

            SetState(NexusSessionStatus.Verifying, User, null);
            RaiseChanged();
            await ValidateStoredKeyAsync(key, cancellationToken).ConfigureAwait(false);
            return User;
        }
        finally
        {
            _gate.Release();
            RaiseChanged();
        }
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _store.ClearAsync(cancellationToken).ConfigureAwait(false);
            _holder.Set(null);
            SetState(NexusSessionStatus.LoggedOut, null, null);
            _logger.LogInformation("Logged out of Nexus Mods; the stored key was removed");
        }
        finally
        {
            _gate.Release();
        }

        RaiseChanged();
    }

    /// <summary>Validates <paramref name="key"/>: accepted → LoggedIn; rejected → key removed, LoggedOut; unreachable → Unverified. Runs under the gate.</summary>
    private async Task ValidateStoredKeyAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            var user = await _client.ValidateAsync(key, cancellationToken).ConfigureAwait(false);
            SetState(NexusSessionStatus.LoggedIn, user, null);
        }
        catch (NexusAuthException ex)
        {
            _logger.LogWarning("The stored Nexus API key was rejected ({Message}); removing it", ex.Message);
            _holder.Set(null);
            try
            {
                await _store.ClearAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception clearError) when (clearError is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(clearError, "The rejected key could not be deleted from disk");
            }

            SetState(NexusSessionStatus.LoggedOut, null, "Nexus Mods no longer accepts the stored API key. Log in again with a new key.");
        }
        catch (NexusException ex)
        {
            // Offline / Nexus down / rate limited: keep the key, work with it, show "not verified".
            _logger.LogWarning("The stored Nexus API key could not be checked ({Message}); using it unverified", ex.Message);
            SetState(NexusSessionStatus.Unverified, User, ex.Message);
        }
    }

    private void SetState(NexusSessionStatus status, NexusUser? user, string? error)
    {
        Status = status;
        User = user;
        LastError = error;
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A Changed handler of the Nexus session threw");
        }
    }
}
