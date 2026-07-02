using System;

namespace Wholphin.Engine.Personalization;

/// <summary>
/// Buckets an hour-of-day into a daypart. Hours are interpreted in UTC to match how the behavior
/// layer captures the event hour (see BehaviorEntryPoint.BuildContext), so the daypart a signal is
/// learned under always equals the daypart it is later scored under. (Switching both to the user's
/// local time is a future refinement; consistency between capture and selection is what matters.)
/// </summary>
public static class Daypart
{
    /// <summary>05:00–11:59.</summary>
    public const string Morning = "Morning";

    /// <summary>12:00–16:59.</summary>
    public const string Afternoon = "Afternoon";

    /// <summary>17:00–21:59.</summary>
    public const string Evening = "Evening";

    /// <summary>22:00–04:59.</summary>
    public const string Night = "Night";

    /// <summary>Returns the daypart label for an hour (0–23).</summary>
    /// <param name="hour">The hour of day.</param>
    /// <returns>The daypart label.</returns>
    public static string Of(int hour) => hour switch
    {
        >= 5 and <= 11 => Morning,
        >= 12 and <= 16 => Afternoon,
        >= 17 and <= 21 => Evening,
        _ => Night,
    };

    /// <summary>Returns the current daypart (UTC).</summary>
    /// <returns>The current daypart label.</returns>
    public static string Current() => Of(DateTime.UtcNow.Hour);
}
