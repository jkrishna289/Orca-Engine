using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wholphin.Engine.Data.Entities;

namespace Wholphin.Engine.Home;

/// <summary>
/// Powers the "New Since You Were Away" row: catalog items added (or whose episodes aired/downloaded)
/// since the user's last visit. Last-seen is tracked per user in the roaming settings store.
/// </summary>
public interface INewSinceProvider
{
    /// <summary>Returns items new since the user's last-seen timestamp, newest first.</summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <param name="limit">Maximum items.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The new items (empty when none / no user).</returns>
    Task<IReadOnlyList<CatalogItem>> GetAsync(Guid userId, int limit, CancellationToken ct = default);

    /// <summary>Stamps the user's last-seen time to now (call after a real home visit).</summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the stamp is persisted.</returns>
    Task MarkSeenAsync(Guid userId, CancellationToken ct = default);
}
