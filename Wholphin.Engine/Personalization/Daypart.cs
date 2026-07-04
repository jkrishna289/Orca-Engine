using System;

namespace Wholphin.Engine.Personalization;

/// <summary>
/// Buckets an hour-of-day into a daypart. Hours are interpreted in the household's local time
/// (see <see cref="HouseholdClock"/>) — both the behavior layer's capture
/// (BehaviorEntryPoint.BuildContext) and selection use the same zone, so a signal is scored under
/// the daypart it was learned in. <see cref="Of"/> stays a pure function of the hour.
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

    /// <summary>Returns the current daypart in the household time zone.</summary>
    /// <returns>The current daypart label.</returns>
    public static string Current() => Of(HouseholdClock.Hour());
}
