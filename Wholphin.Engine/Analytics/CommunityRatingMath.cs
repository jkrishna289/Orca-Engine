using System;
using System.Collections.Generic;
using Wholphin.Engine.Data.Enums;

namespace Wholphin.Engine.Analytics;

/// <summary>
/// Pure, testable math for the Wholphin community rating — a local 0-10 score aggregated from
/// behavior signals, Bayesian-shrunk toward a prior (the item's TMDB rating) so a single vote can't
/// swing the score. Stateless; the service handles I/O.
/// </summary>
public static class CommunityRatingMath
{
    /// <summary>Strength of the prior (in "virtual votes") the score is shrunk toward.</summary>
    public const double PriorWeight = 3.0;

    /// <summary>Fallback prior mean when the item has no TMDB rating.</summary>
    public const double DefaultPrior = 6.5;

    /// <summary>Maps a behavior signal to an implied rating (0-10) + a confidence weight, or null to ignore it.</summary>
    /// <param name="type">The behavior event type.</param>
    /// <param name="value">The event value (a 0-10 rating for <see cref="BehaviorEventType.Rated"/>).</param>
    /// <returns>The (rating, weight) pair, or null when the signal carries no rating opinion.</returns>
    public static (double Rating, double Weight)? Implied(BehaviorEventType type, double value) => type switch
    {
        BehaviorEventType.Rated => (Math.Clamp(value, 0.0, 10.0), 1.0),
        BehaviorEventType.ThumbsUp => (9.0, 1.0),
        BehaviorEventType.ThumbsDown => (2.0, 1.0),
        BehaviorEventType.MarkedFavorite => (8.5, 0.6),
        BehaviorEventType.PlaybackCompleted => (7.0, 0.3),
        _ => null,
    };

    /// <summary>
    /// Aggregates implied signals into a Bayesian-averaged score and a vote count, or null when there
    /// are no signals.
    /// </summary>
    /// <param name="signals">The (rating, weight) signals for one item.</param>
    /// <param name="prior">The prior mean (e.g., the item's TMDB rating); defaults when out of range.</param>
    /// <returns>The (score, votes) pair, or null when there are no signals.</returns>
    public static (double Score, int Votes)? Aggregate(IEnumerable<(double Rating, double Weight)> signals, double? prior)
    {
        double sumWeight = 0;
        double sumWeightedRating = 0;
        var votes = 0;
        foreach (var (rating, weight) in signals)
        {
            sumWeight += weight;
            sumWeightedRating += weight * rating;
            votes++;
        }

        if (votes == 0)
        {
            return null;
        }

        var priorMean = prior is { } p and >= 0.0 and <= 10.0 ? p : DefaultPrior;
        var score = ((PriorWeight * priorMean) + sumWeightedRating) / (PriorWeight + sumWeight);
        return (Math.Round(score, 2), votes);
    }
}
