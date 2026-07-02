using System;
using Wholphin.Engine.Data.Enums;

namespace Wholphin.Engine.Behavior;

/// <summary>
/// Default per-signal affinity weights. A single source of truth for how strongly each
/// behavior signal nudges a user's affinity vectors. Consumed by the personalization
/// engine (milestone task #5); kept here next to the capture layer so the capture and
/// scoring meaning of an event never drift apart.
/// </summary>
/// <remarks>
/// Calibrated from the agreed signal model: explicit thumbs are the strongest signal,
/// favorite ~ a five-star rating (+10), finished +5, request +4, early-abandon -3.
/// These are starting values; the admin/config layer (task #8) will make them tunable.
/// </remarks>
public static class SignalWeights
{
    /// <summary>Completion fraction below which a stop counts as an early abandon (negative signal).</summary>
    public const double EarlyAbandonThreshold = 0.20;

    /// <summary>Completion fraction at or above which a stop counts as effectively finished.</summary>
    public const double NearCompleteThreshold = 0.80;

    /// <summary>
    /// Returns the affinity weight contribution for a captured event.
    /// </summary>
    /// <param name="type">The event type.</param>
    /// <param name="value">The event's value (completion fraction for stops, rating for <see cref="BehaviorEventType.Rated"/>).</param>
    /// <returns>A signed weight; positive boosts the item's traits, negative penalizes them.</returns>
    public static double For(BehaviorEventType type, double value) => type switch
    {
        BehaviorEventType.ThumbsUp => 12.0,
        BehaviorEventType.ThumbsDown => -12.0,
        BehaviorEventType.MarkedFavorite => 10.0,
        BehaviorEventType.UnmarkedFavorite => -4.0,

        // Rating is 0..10; centre on 5 so a 5/10 is neutral and 10/10 ≈ a favorite.
        BehaviorEventType.Rated => (value - 5.0) * 2.0,

        BehaviorEventType.PlaybackCompleted => 5.0,
        BehaviorEventType.MarkedPlayed => 3.0,
        BehaviorEventType.MarkedUnplayed => -2.0,

        // Stops carry the completion fraction: early abandon penalizes, near-complete rewards,
        // the middle scales linearly with how much was watched.
        BehaviorEventType.PlaybackStopped => value < EarlyAbandonThreshold ? -3.0
            : value >= NearCompleteThreshold ? 4.0
            : value * 2.0,

        BehaviorEventType.RequestCreated => 4.0,
        BehaviorEventType.RequestFulfilled => 1.0,

        BehaviorEventType.PlaybackStarted => 0.5,
        BehaviorEventType.Searched => 0.5,
        BehaviorEventType.Browsed => 0.1,

        // Home telemetry (Feature A): focus/dwell is a mild interest signal even without playback;
        // a click is stronger. Impressions are captured for funnel/negative analysis only (no
        // positive taste weight, so the sheer volume of "shown" cards can't swamp the profile).
        BehaviorEventType.CardImpression => 0.0,
        BehaviorEventType.CardFocused => Math.Clamp(value / 20.0, 0.0, 0.4),
        BehaviorEventType.CardClicked => 0.3,

        _ => 0.0
    };
}
