using System;
using Wholphin.Engine.Data.Entities;
using Wholphin.Engine.Data.Enums;

namespace Wholphin.Engine.Discovery;

/// <summary>
/// The pure math behind recommendation memory (<see cref="UserItemMemory"/>): a continuous
/// per-(user, title) interest score that decays when recommendations are ignored, recovers on
/// engagement, and heals with time. A hard cooldown emerges when the score crosses a threshold —
/// temporary and threshold-derived, never a permanent flag. Stateless and deterministic
/// (precedent: <see cref="Behavior.SignalWeights"/>, <see cref="Behavior.NegativeSignals"/>).
/// </summary>
/// <remarks>
/// Division of labor: <see cref="Behavior.NegativeSignals"/> shapes the user's <em>taste</em>
/// (what kinds of content to pull); this model shapes <em>per-item resurfacing</em> (whether this
/// specific title may be recommended again, and how strongly).
/// </remarks>
public static class InterestModel
{
    /// <summary>Multiplier applied to the interest score for each recommendation cycle the user ignored.</summary>
    public const double IgnoredCycleDecay = 0.7;

    /// <summary>Interest score below which a hard cooldown starts.</summary>
    public const double CooldownThreshold = 0.35;

    /// <summary>Additive interest restored by a light engagement (click, trailer, focus).</summary>
    public const double EngagementBoost = 0.3;

    /// <summary>Half-recovery period: how long until half the lost interest heals on its own.</summary>
    public const double RecoveryHalfLifeDays = 45.0;

    /// <summary>Per-repeat exposure penalty step (multiplied by capped times-recommended).</summary>
    public const double ExposurePenaltyStep = 0.05;

    /// <summary>Cap on the exposure-penalty multiplier.</summary>
    public const int ExposurePenaltyCap = 4;

    /// <summary>How long a hard cooldown lasts once triggered.</summary>
    public static readonly TimeSpan CooldownDuration = TimeSpan.FromDays(14);

    /// <summary>
    /// The effective interest at read time: the stored score plus time-recovery toward 1.0
    /// (computed at read so healing needs no background writes).
    /// </summary>
    /// <param name="memory">The memory record, or null when the title was never recommended.</param>
    /// <param name="now">The current UTC time.</param>
    /// <returns>The effective interest multiplier in [0, 1].</returns>
    public static double EffectiveInterest(UserItemMemory? memory, DateTime now)
    {
        if (memory is null)
        {
            return 1.0;
        }

        var days = Math.Max(0, (now - memory.UpdatedAt).TotalDays);
        var lost = 1.0 - Math.Clamp(memory.InterestScore, 0.0, 1.0);
        return 1.0 - (lost * Math.Pow(0.5, days / RecoveryHalfLifeDays));
    }

    /// <summary>The repeated-exposure penalty for scoring (0 when never recommended).</summary>
    /// <param name="memory">The memory record, or null.</param>
    /// <returns>The penalty to subtract from the final score.</returns>
    public static double ExposurePenalty(UserItemMemory? memory)
        => memory is null ? 0.0 : ExposurePenaltyStep * Math.Min(memory.TimesRecommended, ExposurePenaltyCap);

    /// <summary>Whether the title sits in an active hard cooldown (eligibility drop).</summary>
    /// <param name="memory">The memory record, or null.</param>
    /// <param name="now">The current UTC time.</param>
    /// <returns>True while the cooldown is active.</returns>
    public static bool IsInCooldown(UserItemMemory? memory, DateTime now)
        => memory?.CooldownUntil is { } until && until > now;

    /// <summary>Records that the title was recommended (a pick was written) this cycle.</summary>
    /// <param name="memory">The memory record to update.</param>
    /// <param name="now">The current UTC time.</param>
    public static void OnRecommended(UserItemMemory memory, DateTime now)
    {
        memory.TimesRecommended++;
        memory.LastRecommendedAt = now;
        memory.UpdatedAt = now;
    }

    /// <summary>
    /// Records a positive engagement. Strong signals (request, playback) reset interest fully;
    /// light signals (click, trailer, focus) restore it additively.
    /// </summary>
    /// <param name="memory">The memory record to update.</param>
    /// <param name="type">The engagement event type.</param>
    /// <param name="now">The current UTC time.</param>
    public static void OnEngaged(UserItemMemory memory, BehaviorEventType type, DateTime now)
    {
        memory.InterestScore = type is BehaviorEventType.RequestCreated
            or BehaviorEventType.PlaybackStarted
            or BehaviorEventType.PlaybackCompleted
            ? 1.0
            : Math.Min(1.0, EffectiveInterest(memory, now) + EngagementBoost);
        memory.Engagements++;
        memory.LastEngagedAt = now;
        memory.CooldownUntil = null;
        memory.UpdatedAt = now;
    }

    /// <summary>
    /// Records one card impression (the title was actually on screen). Materializes the
    /// time-recovered score first so bumping <see cref="UserItemMemory.UpdatedAt"/> never erases
    /// healing progress.
    /// </summary>
    /// <param name="memory">The memory record to update.</param>
    /// <param name="now">The current UTC time.</param>
    public static void OnImpression(UserItemMemory memory, DateTime now)
    {
        memory.InterestScore = EffectiveInterest(memory, now);
        memory.Impressions++;
        memory.UpdatedAt = now;
    }

    /// <summary>
    /// Records one ignored recommendation cycle (a pick expired with zero engagement while live):
    /// decays the interest score and starts a cooldown once it crosses the threshold.
    /// </summary>
    /// <param name="memory">The memory record to update.</param>
    /// <param name="now">The current UTC time.</param>
    public static void OnIgnoredCycle(UserItemMemory memory, DateTime now)
    {
        memory.InterestScore = EffectiveInterest(memory, now) * IgnoredCycleDecay;
        if (memory.InterestScore < CooldownThreshold)
        {
            memory.CooldownUntil = now + CooldownDuration;
        }

        memory.UpdatedAt = now;
    }
}
