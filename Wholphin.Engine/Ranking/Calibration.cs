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

    /// <summary>The average a rating is pulled toward (TMDB's catalogue mean is about 6.5).</summary>
    public const double RatingPrior = 6.5;

    /// <summary>
    /// Votes at which a title's own average and the prior count equally. Low on purpose: Hindi,
    /// Tamil or Korean titles have far fewer TMDB votes than Hollywood ones, and a higher bar would
    /// push a whole national cinema toward the average along with the junk.
    /// </summary>
    public const int RatingPriorVotes = 50;

    /// <summary>
    /// A rating weighted by how many votes it rests on (a Bayesian average), so a 10.0 from three
    /// votes stops outranking an 8.2 from fifty thousand: (v·R + m·C) / (v + m).
    /// </summary>
    /// <param name="rating">The raw 0..10 rating.</param>
    /// <param name="votes">The vote count; null = not fetched yet, weighed as <see cref="RatingPriorVotes"/>.</param>
    /// <returns>The weighted rating, or null when there's no real rating (none, or zero votes).</returns>
    public static double? WeightedRating(float? rating, int? votes)
    {
        if (rating is not { } r || votes == 0)
        {
            return null;
        }

        var v = Math.Max(0, votes ?? RatingPriorVotes);
        return ((v * r) + (RatingPriorVotes * RatingPrior)) / (v + RatingPriorVotes);
    }
}
