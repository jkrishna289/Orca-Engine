namespace Wholphin.Engine.Ranking;

/// <summary>
/// The blend weights for the shared scoring vocabulary. One immutable weight set per ranker
/// (recommender vs discovery); the factory defaults reproduce the historical hardcoded constants
/// exactly, and <see cref="IScoringPolicy"/> layers admin config on top. Components a ranker does
/// not use are simply weighted 0.
/// </summary>
public sealed class ScoringWeights
{
    /// <summary>Gets the weight on the taste/personalization component.</summary>
    public double Taste { get; init; }

    /// <summary>Gets the weight on the quality (rating) component.</summary>
    public double Quality { get; init; }

    /// <summary>Gets the weight on the popularity component.</summary>
    public double Popularity { get; init; }

    /// <summary>Gets the weight on the freshness/recency component.</summary>
    public double Freshness { get; init; }

    /// <summary>Gets the weight on the novelty (exploration) component.</summary>
    public double Novelty { get; init; }

    /// <summary>Gets the weight on the availability component.</summary>
    public double Availability { get; init; }

    /// <summary>Gets the weight on the source-confidence component.</summary>
    public double SourceConfidence { get; init; }

    /// <summary>The default local-recommender weights (blueprint constants: 0.60 / 0.15 / 0.05 / 0.10).</summary>
    public static ScoringWeights DefaultRecommender => new()
    {
        Taste = 0.60,
        Quality = 0.15,
        Freshness = 0.05,
        Availability = 0.10,
    };

    /// <summary>The default discovery weights (0.55 taste / 0.20 popularity / 0.10 freshness / 0.10 novelty / 0.05 source).</summary>
    public static ScoringWeights DefaultDiscovery => new()
    {
        Taste = 0.55,
        Popularity = 0.20,
        Freshness = 0.10,
        Novelty = 0.10,
        SourceConfidence = 0.05,
    };
}
