using System;

namespace Wholphin.Engine.Personalization;

/// <summary>
/// Accepts "this user's profile is stale" signals for asynchronous, debounced recomputation.
/// Enqueuing is non-blocking so it is safe to call from live web requests and event handlers.
/// </summary>
public interface IProfileRecomputeQueue
{
    /// <summary>
    /// Marks a user's profile dirty. The background worker coalesces bursts and recomputes once.
    /// </summary>
    /// <param name="userId">The Jellyfin user id whose affinity should be recomputed.</param>
    void Enqueue(Guid userId);
}
