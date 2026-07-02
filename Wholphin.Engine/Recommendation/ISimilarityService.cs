using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wholphin.Engine.Data.Entities;

namespace Wholphin.Engine.Recommendation;

/// <summary>A scored similar title.</summary>
/// <param name="Item">The similar catalog item.</param>
/// <param name="Score">The similarity score (0..1).</param>
public record SimilarityResult(CatalogItem Item, double Score);

/// <summary>The "Because You Watched" payload: the seed the user watched plus titles like it.</summary>
/// <param name="Seed">The most recently watched item that seeded the row, or null when none.</param>
/// <param name="Items">The similar titles (excluding the seed and already-watched items).</param>
public record BecauseYouWatched(CatalogItem? Seed, IReadOnlyList<SimilarityResult> Items);

/// <summary>
/// Content-based item-to-item similarity over the catalog. Powers "More Like This" / "Similar
/// Titles" (per item) and "Because You Watched X" (per user). Computed on demand and cached, so it
/// needs no schema change; a persisted graph is a later optimization.
/// </summary>
public interface ISimilarityService
{
    /// <summary>Returns the titles most similar to a catalog item.</summary>
    /// <param name="catalogItemId">The source catalog item id.</param>
    /// <param name="limit">Maximum results (1-100).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The similar titles, highest score first (empty when the source is unknown).</returns>
    Task<IReadOnlyList<SimilarityResult>> MoreLikeThisAsync(long catalogItemId, int limit, CancellationToken ct = default);

    /// <summary>Builds a "Because You Watched X" set seeded by the user's most recent watch.</summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <param name="limit">Maximum results (1-100).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The seed and similar titles (seed null when the user has no watch history).</returns>
    Task<BecauseYouWatched> BecauseYouWatchedAsync(Guid userId, int limit, CancellationToken ct = default);
}
