using System;
using Wholphin.Engine.Data.Entities;
using Wholphin.Engine.Personalization;

namespace Wholphin.Engine.Recommendation;

/// <summary>
/// Pure novelty scoring for the exploration engine (Feature K). Novelty = quality × unfamiliarity:
/// it favors high-quality titles in genres the user is neutral on, returns 0 for genres they
/// already love (not novel) or actively dislike (don't push them), and 0 for unrated/genreless
/// items. Stateless and deterministic (unit-testable).
/// </summary>
public static class ExplorationScorer
{
    /// <summary>Computes a novelty/serendipity score in [0, 1] for an item given a user's affinity.</summary>
    /// <param name="item">The candidate item.</param>
    /// <param name="affinity">The user's affinity vector.</param>
    /// <returns>The novelty score (0 = not a good exploration pick).</returns>
    public static double Novelty(CatalogItem item, AffinityVector affinity)
    {
        var quality = Math.Clamp((item.CommunityRating ?? 0) / 10.0, 0, 1);
        if (quality <= 0)
        {
            return 0;
        }

        var genres = CatalogFeatures.Parse(item.GenresJson);
        if (genres.Count == 0)
        {
            return 0;
        }

        // Seed at -inf (not 0) so an all-disliked item yields a negative max and is skipped below;
        // unknown genres contribute 0 (GetValueOrDefault), which reads as "neutral / novel".
        double maxAffinity = double.NegativeInfinity;
        foreach (var g in genres)
        {
            maxAffinity = Math.Max(maxAffinity, affinity.Genre.GetValueOrDefault(g));
        }

        if (maxAffinity < 0)
        {
            return 0; // don't "explore" into genres the user has shown dislike for
        }

        var unfamiliarity = Math.Clamp(1 - maxAffinity, 0, 1);
        return quality * unfamiliarity;
    }
}
