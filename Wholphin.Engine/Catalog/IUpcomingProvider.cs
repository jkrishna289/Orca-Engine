using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wholphin.Engine.Data.Entities;

namespace Wholphin.Engine.Catalog;

/// <summary>One upcoming title: a catalog item, when it airs/releases, and a short display label.</summary>
public record UpcomingItem(CatalogItem Item, DateTime AirDateUtc, string Label);

/// <summary>
/// Powers the "Coming Soon (For You)" row: upcoming episodes/releases for catalog titles from the
/// *arr calendar (primary) with a TMDB next-episode fallback, then <em>filtered by relevance</em> —
/// monitored series lead, new seasons of invested-in shows follow, and the rest must clear an intent
/// + quality bar (<see cref="ComingSoonClassifier"/>). Cached per user, since air dates move slowly.
/// </summary>
public interface IUpcomingProvider
{
    /// <summary>Returns relevant upcoming items (soonest first), capped at <paramref name="limit"/>.</summary>
    /// <param name="userId">The user to personalize for (null = anonymous/global).</param>
    /// <param name="limit">Maximum items.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The upcoming items (empty when nothing is configured/scheduled/relevant).</returns>
    Task<IReadOnlyList<UpcomingItem>> GetUpcomingAsync(Guid? userId, int limit, CancellationToken ct = default);
}
