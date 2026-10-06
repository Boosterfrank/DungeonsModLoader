using System.Globalization;
using System.Net.Http.Headers;

namespace DungeonsModLoader.Nexus.Api;

/// <summary>
/// The <c>X-RL-*</c> headers of the last v1 response: 2,500 requests per day, then 100 per hour, per user.
/// The two reset timestamps are documented in different formats, so both are parsed leniently.
/// </summary>
public sealed record NexusRateLimit(
    int? HourlyLimit,
    int? HourlyRemaining,
    DateTimeOffset? HourlyReset,
    int? DailyLimit,
    int? DailyRemaining,
    DateTimeOffset? DailyReset,
    DateTimeOffset ObservedAt)
{
    /// <summary>True when the server reported no requests left in the hour or the day.</summary>
    public bool IsExhausted => HourlyRemaining == 0 || DailyRemaining == 0;

    /// <summary>When the exhausted budget comes back, if known.</summary>
    public DateTimeOffset? ResumeAt
    {
        get
        {
            if (DailyRemaining == 0 && DailyReset is { } daily && HourlyRemaining != 0)
            {
                return daily;
            }

            if (HourlyRemaining == 0)
            {
                return HourlyReset;
            }

            return DailyRemaining == 0 ? DailyReset : null;
        }
    }

    /// <summary>Reads the headers; returns null when none of them is present (GraphQL responses carry none).</summary>
    public static NexusRateLimit? FromHeaders(HttpResponseHeaders headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        var hourlyLimit = ReadInt(headers, "X-RL-Hourly-Limit");
        var hourlyRemaining = ReadInt(headers, "X-RL-Hourly-Remaining");
        var hourlyReset = ReadDate(headers, "X-RL-Hourly-Reset");
        var dailyLimit = ReadInt(headers, "X-RL-Daily-Limit");
        var dailyRemaining = ReadInt(headers, "X-RL-Daily-Remaining");
        var dailyReset = ReadDate(headers, "X-RL-Daily-Reset");

        if (hourlyLimit is null && hourlyRemaining is null && dailyLimit is null && dailyRemaining is null)
        {
            return null;
        }

        return new NexusRateLimit(hourlyLimit, hourlyRemaining, hourlyReset, dailyLimit, dailyRemaining, dailyReset, DateTimeOffset.UtcNow);
    }

    private static int? ReadInt(HttpResponseHeaders headers, string name)
    {
        if (!headers.TryGetValues(name, out var values))
        {
            return null;
        }

        var text = values.FirstOrDefault()?.Trim();
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    /// <summary>Accepts "2019-02-01T12:00:00+00:00" and "2019-02-02 00:00:00 +0000".</summary>
    internal static DateTimeOffset? ParseDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        text = text.Trim();
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value))
        {
            return value;
        }

        string[] formats = ["yyyy-MM-dd HH:mm:ss zzz", "yyyy-MM-dd HH:mm:ss zz", "yyyy-MM-dd HH:mm:ss K"];
        var normalized = text;
        // "+0000" -> "+00:00" so the zzz format matches.
        if (normalized.Length > 5 && (normalized[^5] is '+' or '-') && normalized[^4..].All(char.IsDigit))
        {
            normalized = normalized[..^2] + ":" + normalized[^2..];
        }

        return DateTimeOffset.TryParseExact(normalized, formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out value) ? value : null;
    }

    private static DateTimeOffset? ReadDate(HttpResponseHeaders headers, string name) =>
        headers.TryGetValues(name, out var values) ? ParseDate(values.FirstOrDefault()) : null;
}
