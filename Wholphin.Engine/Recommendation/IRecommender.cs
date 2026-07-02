using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Wholphin.Engine.Recommendation;

/// <summary>
/// Produces personalized, ranked recommendations from the unified catalog.
/// </summary>
public interface IRecommender
{
    /// <summary>
    /// Recommends items for a user: content-based candidate generation, confidence-blended
    /// weighted scoring, and diversity-aware re-ranking. Excludes already-finished and
    /// thumbs-down'd items.
    /// </summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <param name="limit">Maximum number of recommendations.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Scored recommendations, best first.</returns>
    Task<IReadOnlyList<RecommendationResult>> RecommendAsync(Guid userId, int limit, CancellationToken ct = default);
}
