namespace DungeonsModLoader.Nexus.Auth;

public enum NexusAuthMethod
{
    /// <summary>The user pastes a personal API key from the Nexus "API Access" page (testing / personal use).</summary>
    PersonalApiKey,

    /// <summary>"Log in with Nexus": browser approval over the SSO websocket; needs the app slug issued by Nexus.</summary>
    Sso,
}

/// <summary>
/// A way to obtain the user's API key. The session validates and stores whatever a provider returns, so the two
/// implementations only differ in how the key is acquired.
/// </summary>
public interface INexusAuthProvider
{
    NexusAuthMethod Method { get; }

    /// <summary>False when the provider cannot be used in this build (SSO without a registered slug).</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Returns an API key. For <see cref="NexusAuthMethod.PersonalApiKey"/>, <paramref name="userInput"/> is the key
    /// the user pasted; for SSO it is ignored, the browser is opened and the call completes once the user approved.
    /// Throws <see cref="Api.NexusException"/> subclasses with user-facing messages.
    /// </summary>
    Task<string> AcquireApiKeyAsync(string? userInput, IProgress<string>? progress = null, CancellationToken cancellationToken = default);
}

/// <summary>Opens a URL in the user's default browser (implemented by the app layer).</summary>
public interface IUrlOpener
{
    void OpenUrl(string url);
}

/// <summary><see cref="NexusAuthMethod.PersonalApiKey"/>: trims and sanity-checks the pasted key.</summary>
public sealed class PersonalApiKeyAuthProvider : INexusAuthProvider
{
    public NexusAuthMethod Method => NexusAuthMethod.PersonalApiKey;

    public bool IsAvailable => true;

    public Task<string> AcquireApiKeyAsync(string? userInput, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var key = userInput?.Trim() ?? string.Empty;
        if (key.Length == 0)
        {
            throw new Api.NexusException("Paste your personal API key first. You can create one on the Nexus Mods \"API Access\" page.");
        }

        if (key.Any(char.IsWhiteSpace) || key.Length < 16)
        {
            throw new Api.NexusException("That does not look like a Nexus Mods API key. Copy the whole key from the \"API Access\" page and try again.");
        }

        return Task.FromResult(key);
    }
}
