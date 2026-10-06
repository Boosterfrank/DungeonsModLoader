using System.Globalization;

namespace DungeonsModLoader.App.ViewModels;

/// <summary>Small display helpers shared by the page view models.</summary>
public static class Format
{
    /// <summary>"512 B", "48 KB", "12.4 MB", "1.25 GB".</summary>
    public static string Size(long bytes)
    {
        const double kilobyte = 1024;
        const double megabyte = kilobyte * 1024;
        const double gigabyte = megabyte * 1024;
        var culture = CultureInfo.CurrentCulture;
        if (bytes < kilobyte)
        {
            return string.Create(culture, $"{bytes} B");
        }

        if (bytes < megabyte)
        {
            return string.Create(culture, $"{bytes / kilobyte:0} KB");
        }

        if (bytes < gigabyte)
        {
            return string.Create(culture, $"{bytes / megabyte:0.#} MB");
        }

        return string.Create(culture, $"{bytes / gigabyte:0.##} GB");
    }

    /// <summary>"1.2k", "35k", "2.1M" for counters; small numbers unchanged.</summary>
    public static string Count(long value)
    {
        var culture = CultureInfo.CurrentCulture;
        return value switch
        {
            < 1000 => value.ToString(culture),
            < 10_000 => string.Create(culture, $"{value / 1000.0:0.#}k"),
            < 1_000_000 => string.Create(culture, $"{value / 1000.0:0}k"),
            _ => string.Create(culture, $"{value / 1_000_000.0:0.#}M"),
        };
    }

    /// <summary>"just now", "5 minutes ago", "yesterday", "3 days ago", else a short date.</summary>
    public static string Relative(DateTimeOffset when)
    {
        var age = DateTimeOffset.UtcNow - when;
        if (age < TimeSpan.FromMinutes(1))
        {
            return "just now";
        }

        if (age < TimeSpan.FromHours(1))
        {
            var minutes = (int)age.TotalMinutes;
            return minutes == 1 ? "1 minute ago" : $"{minutes} minutes ago";
        }

        if (age < TimeSpan.FromDays(1))
        {
            var hours = (int)age.TotalHours;
            return hours == 1 ? "1 hour ago" : $"{hours} hours ago";
        }

        if (age < TimeSpan.FromDays(7))
        {
            var days = (int)age.TotalDays;
            return days == 1 ? "yesterday" : $"{days} days ago";
        }

        if (age < TimeSpan.FromDays(60))
        {
            var weeks = (int)(age.TotalDays / 7);
            return weeks == 1 ? "1 week ago" : $"{weeks} weeks ago";
        }

        return when.ToLocalTime().ToString("d", CultureInfo.CurrentCulture);
    }

    /// <summary>"12.4 MB of 48 MB · 3.1 MB/s" for the download dialog.</summary>
    public static string Download(long received, long? total, double bytesPerSecond)
    {
        var text = total is { } t && t > 0 ? $"{Size(received)} of {Size(t)}" : Size(received);
        return bytesPerSecond > 1024 ? $"{text} · {Size((long)bytesPerSecond)}/s" : text;
    }
}
