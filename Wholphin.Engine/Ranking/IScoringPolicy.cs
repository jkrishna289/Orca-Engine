namespace Wholphin.Engine.Ranking;

/// <summary>
/// The single source of truth for ranking weights. Resolves the recommender and discovery weight
/// sets from admin configuration (defaults = the historical constants; every weight clamped to a
/// sane range), so tuning one knob moves every ranker that uses it and the duplicated per-scorer
/// constants are gone.
/// </summary>
public interface IScoringPolicy
{
    /// <summary>Gets the weights for the local content recommender.</summary>
    ScoringWeights Recommender { get; }

    /// <summary>Gets the weights for the taste-driven discovery scorer.</summary>
    ScoringWeights Discovery { get; }
}
