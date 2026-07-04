namespace Wholphin.Engine.Discovery;

/// <summary>
/// The component breakdown behind a discovery ranking decision. Core components are persisted as
/// real columns on the pick (queryable); the full breakdown is kept in the pick's debug-only
/// explanation JSON. Produced by the scoring stage; <see cref="DiversityPenalty"/> and the final
/// re-computation are applied by the diversity stage.
/// </summary>
public class DiscoveryScore
{
    /// <summary>Gets or sets the taste-similarity component (same-batch embedding cosine + genre-affinity bonus), 0..1.</summary>
    public double Taste { get; set; }

    /// <summary>Gets or sets the normalized popularity component, 0..1.</summary>
    public double Popularity { get; set; }

    /// <summary>Gets or sets the release-recency component, 0..1.</summary>
    public double Freshness { get; set; }

    /// <summary>Gets or sets the novelty (exploration) component — quality × genre unfamiliarity, 0..1.</summary>
    public double Novelty { get; set; }

    /// <summary>Gets or sets the weak contextual country boost (≤ 0.05; boost-only, never primary ranking).</summary>
    public double CountryBoost { get; set; }

    /// <summary>Gets or sets the interest multiplier from recommendation memory (1 = fresh; decays when ignored).</summary>
    public double InterestMultiplier { get; set; } = 1.0;

    /// <summary>Gets or sets the repeated-exposure penalty from recommendation memory.</summary>
    public double ExposurePenalty { get; set; }

    /// <summary>Gets or sets the strongest source confidence among the candidate's attributions, 0..1.</summary>
    public double SourceConfidence { get; set; }

    /// <summary>Gets or sets the diversity penalty applied by the reshuffle stage.</summary>
    public double DiversityPenalty { get; set; }

    /// <summary>Gets or sets the final blended score the pipeline ranks and selects by.</summary>
    public double Final { get; set; }
}
