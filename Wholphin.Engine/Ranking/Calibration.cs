using System;

namespace Wholphin.Engine.Ranking;

/// <summary>
/// Confidence-scaled calibration: a Bayesian shrink of a personal estimate toward a global prior
/// when the engine barely knows the user. Pure + deterministic. Shared by the rankers so
/// "personal vs prior" is blended the same way everywhere.
/// </summary>
public static class Calibration
{
    /// <summary>
    /// Blends a personal signal toward a prior by confidence: at confidence 1 the personal value is
    /// used as-is; at 0 the prior dominates.
    /// </summary>
    /// <param name="confidence">Profile confidence in [0, 1].</param>
    /// <param name="personal">The personalized estimate.</param>
    /// <param name="prior">The global prior (e.g. quality).</param>
    /// <returns>The calibrated value.</returns>
    public static double Shrink(double confidence, double personal, double prior)
    {
        var c = Math.Clamp(confidence, 0.0, 1.0);
        return (c * personal) + ((1 - c) * prior);
    }
}
