using System;

namespace Wholphin.Engine.Behavior;

/// <summary>
/// Derived (implicit) negative signals — the counterpart to <see cref="SignalWeights"/>, but for
/// patterns that span many events rather than a single one. Turns home-funnel behavior into a
/// dis-interest penalty: a title shown repeatedly yet never engaged, or focused several times yet
/// never played, suggests the user is actively skipping it. Kept here next to the positive weights
/// so the scoring meaning stays in one place. Pure + stateless (unit-testable).
/// </summary>
public static class NegativeSignals
{
    /// <summary>Impressions at/above which a never-engaged title counts as ignored.</summary>
    public const int IgnoreImpressionThreshold = 5;

    /// <summary>Base penalty for an ignored title (scaled by how many times it was shown).</summary>
    public const double IgnorePenalty = 2.0;

    /// <summary>Cap on the ignore scaling, so a flood of impressions can't produce a runaway penalty.</summary>
    public const double IgnoreMaxScale = 3.0;

    /// <summary>Focus events at/above which "kept looking, never played" counts as soft rejection.</summary>
    public const int FocusNoPlayThreshold = 4;

    /// <summary>Penalty for a focused-but-never-played title.</summary>
    public const double FocusNoPlayPenalty = 1.0;

    /// <summary>
    /// Computes the dis-interest penalty for a title from its funnel counts. Returns a POSITIVE
    /// magnitude (the caller applies it as a negative contribution); 0 means no penalty.
    /// </summary>
    /// <param name="shown">Impression count.</param>
    /// <param name="focused">Focus count.</param>
    /// <param name="clicked">Click count.</param>
    /// <param name="played">Playback-start/complete/marked count.</param>
    /// <returns>The penalty magnitude (≥ 0).</returns>
    public static double Penalty(int shown, int focused, int clicked, int played)
    {
        var penalty = 0.0;

        // Seen many times but never even focused/clicked/played → clear disinterest.
        if (shown >= IgnoreImpressionThreshold && focused == 0 && clicked == 0 && played == 0)
        {
            penalty += IgnorePenalty * Math.Min(shown / (double)IgnoreImpressionThreshold, IgnoreMaxScale);
        }

        // Kept looking but never committed to watching → soft rejection.
        if (focused >= FocusNoPlayThreshold && clicked == 0 && played == 0)
        {
            penalty += FocusNoPlayPenalty;
        }

        return penalty;
    }
}
