using System;

namespace Wholphin.Engine.Personalization;

/// <summary>
/// Accepts "this user's profile is stale" signals for asynchronous, debounced recomputation.
/// Enqueuing is non-blocking so it is safe to call from live web requests and event handlers.
/// </summary>
public interface IProfileRecomputeQueue
{
    /// <summary>
    /// Gets the number of users waiting to be recomputed.
    /// </summary>
    /// <remarks>
    /// The queue is UNBOUNDED, so this is the only warning that recomputation has stopped keeping
    /// up — a backlog here is invisible everywhere else until memory becomes the symptom.
    /// </remarks>
    int Depth { get; }

    /// <summary>
    /// Marks a user's profile dirty. The background worker coalesces bursts and recomputes once.
    /// </summary>
    /// <param name="userId">The Jellyfin user id whose affinity should be recomputed.</param>
    void Enqueue(Guid userId);
}
