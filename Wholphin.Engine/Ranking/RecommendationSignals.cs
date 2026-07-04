namespace Wholphin.Engine.Ranking;

/// <summary>
/// The shared component breakdown behind any ranking decision — the single vocabulary the local
/// recommender and the discovery pipeline both populate, so "how a score is composed" lives in one
/// place. Positive components are 0..1; the modifiers scale/penalize the blended base.
/// </summary>
public sealed class RecommendationSignals
{
    /// <summary>Gets or sets the taste/personalization component (0..1).</summary>
    public double Taste { get; set; }

    /// <summary>Gets or sets the quality (rating) component (0..1).</summary>
    public double Quality { get; set; }

    /// <summary>Gets or sets the popularity component (0..1).</summary>
    public double Popularity { get; set; }

    /// <summary>Gets or sets the freshness/recency component (0..1).</summary>
    public double Freshness { get; set; }

    /// <summary>Gets or sets the novelty component (0..1).</summary>
    public double Novelty { get; set; }

    /// <summary>Gets or sets the availability component (0..1).</summary>
    public double Availability { get; set; }

    /// <summary>Gets or sets the source-confidence component (0..1).</summary>
    public double SourceConfidence { get; set; }

    /// <summary>Gets or sets a small additive contextual boost (e.g. country); added on top of the weighted base.</summary>
    public double CountryBoost { get; set; }

    /// <summary>Gets or sets the memory interest multiplier (1 = fresh; &lt;1 dampens repeatedly-ignored items).</summary>
    public double InterestMultiplier { get; set; } = 1.0;

    /// <summary>Gets or sets the repeated-exposure penalty subtracted from the score.</summary>
    public double ExposurePenalty { get; set; }

    /// <summary>Gets or sets the diversity penalty subtracted from the score.</summary>
    public double DiversityPenalty { get; set; }

    /// <summary>
    /// The weighted blend: the weighted sum of the 0..1 components plus the country boost, scaled by
    /// the interest multiplier, minus the exposure and diversity penalties.
    /// </summary>
    /// <param name="weights">The ranker's weights.</param>
    /// <returns>The final score (unrounded).</returns>
    public double Blend(ScoringWeights weights)
    {
        var baseScore = (weights.Taste * Taste)
            + (weights.Quality * Quality)
            + (weights.Popularity * Popularity)
            + (weights.Freshness * Freshness)
            + (weights.Novelty * Novelty)
            + (weights.Availability * Availability)
            + (weights.SourceConfidence * SourceConfidence)
            + CountryBoost;
        return (baseScore * InterestMultiplier) - ExposurePenalty - DiversityPenalty;
    }
}
