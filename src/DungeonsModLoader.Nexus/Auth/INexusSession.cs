using DungeonsModLoader.Nexus.Api;

namespace DungeonsModLoader.Nexus.Auth;

public enum NexusSessionStatus
{
    /// <summary>No API key stored. Browsing works through the public GraphQL API; downloads need a key.</summary>
    LoggedOut,

    /// <summary>A key is being checked with Nexus.</summary>
    Verifying,

    /// <summary>The stored key was accepted; <see cref="INexusSession.User"/> is known.</summary>
    LoggedIn,

    /// <summary>A key is stored but Nexus could not be reached to check it (offline). It is used anyway; the next check decides.</summary>
    Unverified,
}

/// <summary>
/// The user's Nexus Mods account in the app: owns the API key (encrypted at rest), validates it on startup,
/// exposes the account details (name, Premium) and performs login / logout. The key itself is only handed to the
/// API client, never to the UI or the log.
/// </summary>
public interface INexusSession
{
    NexusSessionStatus Status { get; }

    /// <summary>The account behind the key, once validated.</summary>
    NexusUser? User { get; }

    bool HasApiKey { get; }

    bool IsPremium { get; }

    /// <summary>What went wrong the last time a key was checked (user-facing), or null.</summary>
    string? LastError { get; }

    /// <summary>The login methods this build offers (personal key always; SSO when the slug is set).</summary>
    IReadOnlyList<INexusAuthProvider> Providers { get; }

    /// <summary>Raised when the status, user or error changed. May be raised on any thread.</summary>
    event EventHandler? Changed;

    /// <summary>Startup: loads the stored key and validates it. Network problems leave the key in place (<see cref="NexusSessionStatus.Unverified"/>).</summary>
    Task RestoreAsync(CancellationToken cancellationToken = default);

    /// <summary>Acquires a key through <paramref name="provider"/>, validates and stores it. Throws <see cref="NexusException"/> with a user-facing message.</summary>
    Task<NexusUser> LoginAsync(INexusAuthProvider provider, string? userInput = null, IProgress<string>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Checks the stored key again ("Verify" when unverified, or to refresh the Premium flag).</summary>
    Task<NexusUser?> RevalidateAsync(CancellationToken cancellationToken = default);

    /// <summary>Removes the stored key.</summary>
    Task LogoutAsync(CancellationToken cancellationToken = default);
}
