using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wholphin.Engine.Data.Entities;

namespace Wholphin.Engine.Home;

/// <summary>
/// A single in-progress ("Continue Watching") item for a user: a catalog projection of the
/// resumable item plus how far into it the user is.
/// </summary>
/// <param name="Item">The catalog projection of the resumable item (a movie or an episode).</param>
/// <param name="Progress">Playback progress as a fraction in the range 0..1.</param>
/// <param name="Subtitle">Optional display subtitle (e.g., "S1:E2 · Episode Name" for episodes).</param>
public sealed record ResumePoint(CatalogItem Item, double Progress, string? Subtitle);

/// <summary>
/// Supplies a user's in-progress items from Jellyfin's per-user playback state. Kept behind a port
/// so the home generator stays decoupled from Jellyfin's library/user-data APIs.
/// </summary>
public interface IResumeProvider
{
    /// <summary>Returns the user's resumable items, most-recently-played first.</summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <param name="limit">Maximum number of items to return.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The resume points (empty when the user is unknown or has nothing in progress).</returns>
    Task<IReadOnlyList<ResumePoint>> GetResumeAsync(Guid userId, int limit, CancellationToken ct);
}
