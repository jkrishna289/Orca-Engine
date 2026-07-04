using Wholphin.Engine.Data.Entities;
using Wholphin.Engine.Personalization;

namespace Wholphin.Engine.Explanation;

/// <summary>
/// Default <see cref="IExplanationService"/> — a thin, injectable seam over the pure
/// <see cref="ExplanationTemplates"/>. Keeping the templates static-and-pure (unit-tested) while
/// exposing an interface lets callers depend on the abstraction and lets future tone/localization
/// changes land in one place.
/// </summary>
public class ExplanationService : IExplanationService
{
    /// <inheritdoc />
    public string ExplainRecommendation(CatalogItem item, AffinityVector affinity)
        => ExplanationTemplates.Recommendation(item, affinity);
}
