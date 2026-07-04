using System;

namespace Wholphin.Engine.Personalization;

/// <summary>
/// Resolves the household's local wall-clock hour for daypart bucketing. Reads the configured
/// time zone (empty = the server's local zone), so signal capture and selection agree on what
/// "evening" means regardless of the server's UTC offset. Fail-soft: an unknown zone id degrades
/// to the server local zone.
/// </summary>
public static class HouseholdClock
{
    /// <summary>Resolves the configured household time zone (server local when unset/invalid).</summary>
    /// <returns>The resolved time zone.</returns>
    public static TimeZoneInfo Resolve()
    {
        var id = Plugin.Instance?.Configuration?.HouseholdTimeZone;
        if (string.IsNullOrWhiteSpace(id))
        {
            return TimeZoneInfo.Local;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.Local;
        }
    }

    /// <summary>Returns the current hour of day (0–23) in the household time zone.</summary>
    /// <returns>The local hour.</returns>
    public static int Hour() => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Resolve()).Hour;
}
