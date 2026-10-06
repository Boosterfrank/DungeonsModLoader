using System.Net;

namespace DungeonsModLoader.Nexus.Api;

/// <summary>Base class of every failure the Nexus client reports. <see cref="Exception.Message"/> is written for the user.</summary>
public class NexusException : Exception
{
    public NexusException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>Nexus Mods could not be reached (offline, DNS, timeout, TLS).</summary>
public sealed class NexusUnavailableException : NexusException
{
    public NexusUnavailableException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>The API answered with an error status.</summary>
public class NexusApiException : NexusException
{
    public NexusApiException(HttpStatusCode statusCode, string message, string? serverMessage = null, Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
        ServerMessage = serverMessage;
    }

    public HttpStatusCode StatusCode { get; }

    /// <summary>The <c>message</c> / <c>error</c> text of the response body, if any (for the details box, not the headline).</summary>
    public string? ServerMessage { get; }
}

/// <summary>The API key is missing, invalid or revoked (401/403 on an authenticated route).</summary>
public sealed class NexusAuthException : NexusApiException
{
    public NexusAuthException(HttpStatusCode statusCode, string message, string? serverMessage = null)
        : base(statusCode, message, serverMessage)
    {
    }
}

/// <summary>The hourly or daily request budget is used up (HTTP 429).</summary>
public sealed class NexusRateLimitException : NexusApiException
{
    public NexusRateLimitException(DateTimeOffset? resumeAt, NexusRateLimit? limit)
        : base(HttpStatusCode.TooManyRequests, BuildMessage(resumeAt))
    {
        ResumeAt = resumeAt;
        Limit = limit;
    }

    /// <summary>When requests may be made again, when the server said so.</summary>
    public DateTimeOffset? ResumeAt { get; }

    public NexusRateLimit? Limit { get; }

    private static string BuildMessage(DateTimeOffset? resumeAt)
    {
        if (resumeAt is null)
        {
            return "Nexus Mods is limiting requests from this account right now. Try again in a few minutes.";
        }

        var wait = resumeAt.Value - DateTimeOffset.UtcNow;
        var minutes = Math.Max(1, (int)Math.Ceiling(wait.TotalMinutes));
        return $"Nexus Mods is limiting requests from this account. Try again in about {minutes} minute{(minutes == 1 ? string.Empty : "s")}.";
    }
}

/// <summary>A download link was requested without the website token a free account needs (HTTP 403).</summary>
public sealed class NexusPremiumRequiredException : NexusException
{
    public NexusPremiumRequiredException()
        : base("Downloads inside the app are a Nexus Mods Premium feature. Free accounts start downloads from the mod's Files page with the \"Mod Manager Download\" button.")
    {
    }
}

/// <summary>The <c>nxm://</c> token has expired (HTTP 410) or does not belong to this account (HTTP 400).</summary>
public sealed class NexusLinkExpiredException : NexusException
{
    public NexusLinkExpiredException(string message)
        : base(message)
    {
    }
}
