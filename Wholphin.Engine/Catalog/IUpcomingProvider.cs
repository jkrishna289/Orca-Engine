using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wholphin.Engine.Data.Entities;

namespace Wholphin.Engine.Catalog;

/// <summary>One upcoming title: a catalog item, when it airs/releases, and a short display label.</summary>
public record UpcomingItem(CatalogItem Item, DateTime AirDateUtc, string Label);

/// <summary>
/// Powers the "Coming Soon" row: upcoming episodes/releases for titles in the catalog, sourced from
/// the *arr calendar (primary) with a TMDB next-episode fallback. Cached, since air dates move slowly.
/// </summary>
public interface IUpcomingProvider
{
    /// <summary>Returns upcoming items (soonest first), capped at <paramref name="limit"/>.</summary>
    /// <param name="limit">Maximum items.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The upcoming items (empty when nothing is configured/scheduled).</returns>
    Task<IReadOnlyList<UpcomingItem>> GetUpcomingAsync(int limit, CancellationToken ct = default);
}
