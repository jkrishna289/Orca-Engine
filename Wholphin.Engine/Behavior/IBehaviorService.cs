using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wholphin.Engine.Data.Entities;

namespace Wholphin.Engine.Behavior;

/// <summary>
/// Captures user-behavior signals into the append-only event log and exposes read helpers.
/// Writes are buffered through a single-reader channel because SQLite is single-writer.
/// </summary>
public interface IBehaviorService
{
    /// <summary>
    /// Enqueues an event for durable, append-only persistence. Non-blocking; safe to call
    /// from Jellyfin event handlers and controllers.
    /// </summary>
    /// <param name="ev">The event to record. <see cref="BehaviorEvent.Timestamp"/> defaults to now if unset.</param>
    void Record(BehaviorEvent ev);

    /// <summary>
    /// Runs the persistence consumer loop until cancelled. Started by the hosted entry point.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the loop stops.</returns>
    Task RunConsumerAsync(CancellationToken ct);

    /// <summary>Returns the most recent events, optionally scoped to a user (newest first).</summary>
    /// <param name="userId">Optional user filter.</param>
    /// <param name="limit">Maximum rows to return.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The recent events.</returns>
    Task<IReadOnlyList<BehaviorEvent>> GetRecentAsync(Guid? userId, int limit, CancellationToken ct = default);

    /// <summary>Returns the total number of logged events, optionally scoped to a user.</summary>
    /// <param name="userId">Optional user filter.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The event count.</returns>
    Task<int> CountAsync(Guid? userId, CancellationToken ct = default);
}
