using System;
using Wholphin.Engine.Configuration;

namespace Wholphin.Engine.Ranking;

/// <summary>
/// Default <see cref="IScoringPolicy"/>. Reads the ranking weights from the live plugin
/// configuration on each access (cheap; config changes take effect without a restart), falling
/// back to the historical defaults per-weight when a configured value is missing or out of range —
/// so a fresh, untouched configuration reproduces the previous behavior exactly.
/// </summary>
public sealed class ScoringPolicy : IScoringPolicy
{
    /// <inheritdoc />
    public ScoringWeights Recommender
    {
        get
        {
            var c = Plugin.Instance?.Configuration;
            var d = ScoringWeights.DefaultRecommender;
            if (c is null)
            {
                return d;
            }

            return new ScoringWeights
            {
                Taste = Weight(c.WeightRecPersonalization, d.Taste),
                Quality = Weight(c.WeightRecQuality, d.Quality),
                Freshness = Weight(c.WeightRecRecency, d.Freshness),
                Availability = Weight(c.WeightRecAvailability, d.Availability),
            };
        }
    }

    /// <inheritdoc />
    public ScoringWeights Discovery
    {
        get
        {
            var c = Plugin.Instance?.Configuration;
            var d = ScoringWeights.DefaultDiscovery;
            if (c is null)
            {
                return d;
            }

            return new ScoringWeights
            {
                Taste = Weight(c.WeightDiscTaste, d.Taste),
                Popularity = Weight(c.WeightDiscPopularity, d.Popularity),
                Freshness = Weight(c.WeightDiscFreshness, d.Freshness),
                Novelty = Weight(c.WeightDiscNovelty, d.Novelty),
                SourceConfidence = Weight(c.WeightDiscSourceConfidence, d.SourceConfidence),
            };
        }
    }

    // Weights are proportions; anything outside [0, 1] is treated as unset and falls back.
    private static double Weight(double configured, double fallback)
        => configured is >= 0.0 and <= 1.0 ? configured : fallback;
}
