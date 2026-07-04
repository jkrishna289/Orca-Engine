using Wholphin.Engine.Data.Entities;
using Wholphin.Engine.Personalization;

namespace Wholphin.Engine.Explanation;

/// <summary>
/// Generates deterministic, human-readable "why recommended" copy from a user's taste and an
/// item's features. The engine's baseline explainability layer: it makes every recommended card
/// self-explaining without any external model. The optional LLM re-ranker, when configured, may
/// override these strings with richer copy — it is an enhancement, never a dependency.
/// </summary>
public interface IExplanationService
{
    /// <summary>Explains a personalized library recommendation for the given user affinity.</summary>
    /// <param name="item">The recommended item.</param>
    /// <param name="affinity">The user's affinity vector.</param>
    /// <returns>A short, deterministic reason string.</returns>
    string ExplainRecommendation(CatalogItem item, AffinityVector affinity);
}
