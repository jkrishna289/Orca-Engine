namespace Wholphin.Engine.Discovery;

/// <summary>
/// Why the eligibility filter dropped a candidate. Every drop is reason-coded so the per-run
/// funnel report (<see cref="Data.Entities.DiscoveryRun"/>) can explain where candidates went.
/// </summary>
public enum FilterReason
{
    /// <summary>The candidate carried no usable TMDB id.</summary>
    InvalidId = 0,

    /// <summary>The title is already in the library (library rows come from library sync, not discovery).</summary>
    InLibrary = 1,

    /// <summary>The title is blacklisted (catalog Availability = Unavailable).</summary>
    Blacklisted = 2,

    /// <summary>The user's recommendation memory has the title in an active hard cooldown.</summary>
    Cooldown = 3,

    /// <summary>The title carries a genre the user actively avoids (affinity below the avoid threshold).</summary>
    AvoidGenre = 4
}
