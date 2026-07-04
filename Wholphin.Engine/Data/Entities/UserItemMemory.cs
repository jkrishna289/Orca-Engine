using System;
using Wholphin.Engine.Data.Enums;

namespace Wholphin.Engine.Data.Entities;

/// <summary>
/// The longitudinal recommendation memory for one (user, title) pair: how often the title has
/// been recommended and shown, whether the user ever engaged, and a continuous interest score
/// that decays when recommendations are ignored and recovers on engagement or with time.
/// Prevents the engine from resurfacing the same ignored titles forever. Added in schema v7.
/// </summary>
/// <remarks>
/// Keyed by TMDB id (not catalog id) so the memory survives catalog sweeps of unreferenced
/// external rows. The interest math lives in the pure <c>Discovery.InterestModel</c>; this entity
/// is only storage. Division of labor: <c>Behavior.NegativeSignals</c> shapes the user's *taste*
/// (affinity vector); this memory shapes *per-item resurfacing* (repetition and cooldown).
/// </remarks>
public class UserItemMemory
{
    /// <summary>Gets or sets the surrogate primary key.</summary>
    public long Id { get; set; }

    /// <summary>Gets or sets the Jellyfin user id.</summary>
    public Guid UserId { get; set; }

    /// <summary>Gets or sets the TMDB id of the remembered title.</summary>
    public int TmdbId { get; set; }

    /// <summary>Gets or sets the media type (movies and series share TMDB id spaces).</summary>
    public MediaType MediaType { get; set; }

    /// <summary>Gets or sets how many pull cycles have recommended this title to the user.</summary>
    public int TimesRecommended { get; set; }

    /// <summary>Gets or sets when the title was last recommended (UTC).</summary>
    public DateTime? LastRecommendedAt { get; set; }

    /// <summary>Gets or sets how many card impressions the user has seen (from app telemetry).</summary>
    public int Impressions { get; set; }

    /// <summary>Gets or sets how many positive engagements occurred (click, trailer, request, watch).</summary>
    public int Engagements { get; set; }

    /// <summary>Gets or sets when the user last engaged with the title (UTC).</summary>
    public DateTime? LastEngagedAt { get; set; }

    /// <summary>
    /// Gets or sets the continuous interest score in [0, 1]. Starts at 1.0; ignored
    /// recommendation cycles decay it, engagement restores it, and time heals it back toward 1.0
    /// (the time recovery is computed at read, never written back).
    /// </summary>
    public double InterestScore { get; set; }

    /// <summary>
    /// Gets or sets the hard-cooldown end (UTC). Set when the interest score crosses the cooldown
    /// threshold; while active the title is ineligible for new picks. Threshold-derived and
    /// temporary — not a permanent suppression flag.
    /// </summary>
    public DateTime? CooldownUntil { get; set; }

    /// <summary>Gets or sets when this record was last written (UTC); drives the time-recovery curve.</summary>
    public DateTime UpdatedAt { get; set; }
}
