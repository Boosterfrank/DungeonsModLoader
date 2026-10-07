namespace DungeonsModLoader.Core.AppUpdates;

/// <summary>When a known newer release stops being optional.</summary>
public static class AppUpdatePolicy
{
    /// <summary>Starts on an outdated version that only show the banner; the next one makes the update mandatory.</summary>
    public const int FreeOutdatedLaunches = 2;

    /// <summary>True when the app has been started more than <see cref="FreeOutdatedLaunches"/> times while outdated (this start included).</summary>
    public static bool IsMandatory(int outdatedLaunchCount) => outdatedLaunchCount > FreeOutdatedLaunches;
}
