using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using DungeonsModLoader.Core;
using Microsoft.Extensions.Logging;

namespace DungeonsModLoader.Nexus.Auth;

/// <summary>Persists the user's API key. The only implementation encrypts it with DPAPI for the current Windows user.</summary>
public interface INexusApiKeyStore
{
    /// <summary>The stored key, or null when none is stored or it cannot be decrypted (different user / machine).</summary>
    Task<string?> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(string apiKey, CancellationToken cancellationToken = default);

    Task ClearAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// <see cref="INexusApiKeyStore"/> writing <c>nexus-apikey.bin</c>: <see cref="ProtectedData"/> with
/// <see cref="DataProtectionScope.CurrentUser"/> plus app-specific entropy, so the file is useless to other
/// users of the PC and to anyone who copies it. The key is never written in plain text or logged.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiNexusApiKeyStore : INexusApiKeyStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DungeonsModLoader.NexusApiKey.v1");

    private readonly string _filePath;
    private readonly ILogger<DpapiNexusApiKeyStore> _logger;

    public DpapiNexusApiKeyStore(AppPaths paths, ILogger<DpapiNexusApiKeyStore> logger)
        : this(paths.NexusApiKeyFile, logger)
    {
    }

    public DpapiNexusApiKeyStore(string filePath, ILogger<DpapiNexusApiKeyStore> logger)
    {
        _filePath = filePath;
        _logger = logger;
    }

    public async Task<string?> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return null;
            }

            var encrypted = await File.ReadAllBytesAsync(_filePath, cancellationToken).ConfigureAwait(false);
            if (encrypted.Length == 0)
            {
                return null;
            }

            var plain = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            var key = Encoding.UTF8.GetString(plain).Trim();
            CryptographicOperations.ZeroMemory(plain);
            _logger.LogDebug("Nexus API key loaded ({Key})", SecretMasker.Describe(key));
            return key.Length == 0 ? null : key;
        }
        catch (CryptographicException ex)
        {
            _logger.LogWarning(ex, "The stored Nexus API key could not be decrypted (it belongs to another Windows user or PC); ignoring it");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "The stored Nexus API key could not be read");
            return null;
        }
    }

    public async Task SaveAsync(string apiKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        var plain = Encoding.UTF8.GetBytes(apiKey.Trim());
        try
        {
            var encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            var temp = _filePath + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
            await File.WriteAllBytesAsync(temp, encrypted, cancellationToken).ConfigureAwait(false);
            File.Move(temp, _filePath, overwrite: true);
            _logger.LogInformation("Nexus API key stored encrypted ({Key})", SecretMasker.Describe(apiKey));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (File.Exists(_filePath))
            {
                File.Delete(_filePath);
                _logger.LogInformation("Nexus API key removed");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "The stored Nexus API key could not be deleted");
            throw;
        }

        return Task.CompletedTask;
    }
}
