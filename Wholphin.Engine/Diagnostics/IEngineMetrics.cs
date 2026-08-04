using System;
using System.Collections.Generic;

namespace Wholphin.Engine.Diagnostics;

/// <summary>
/// Lightweight in-process operational counters (recs served, requests, integration errors, …).
/// Thread-safe and fire-and-forget; surfaced via the admin endpoints for observability. Resets on
/// restart (Tier 1); a durable metrics sink can sit behind this same port later.
/// </summary>
public interface IEngineMetrics
{
    /// <summary>Increments a named counter.</summary>
    /// <param name="key">The counter key (dot-namespaced, e.g. "requests.success").</param>
    /// <param name="by">Amount to add (default 1).</param>
    void Increment(string key, long by = 1);

    /// <summary>
    /// Times one operation: bumps the <c>{key}.count</c> / <c>{key}.total_ms</c> pair and emits a
    /// matching structured event to <see cref="IEngineEvents"/>.
    /// </summary>
    /// <param name="key">The metric namespace, e.g. "home.row.foryou". The first segment becomes the event's component.</param>
    /// <param name="elapsedMs">How long the operation took.</param>
    /// <param name="ok">Whether it succeeded — drives the event level, nothing else.</param>
    /// <param name="data">Short detail for the event. NEVER a URL; TMDB puts the API key in the query string.</param>
    /// <param name="exception">The failure, when there was one.</param>
    /// <remarks>
    /// Deliberately does NOT count outcomes. Call sites already own their own
    /// <c>.error</c>/<c>.timeout</c>/<c>.ok</c> counters and their meanings differ per subsystem
    /// (a row timeout is not a row error), so this stays a pure superset of the count/total pair —
    /// adding it to a call site changes no existing counter.
    /// </remarks>
    void Record(string key, long elapsedMs, bool ok = true, string? data = null, Exception? exception = null);

    /// <summary>Returns a point-in-time copy of all counters (sorted by key).</summary>
    /// <returns>The counter snapshot.</returns>
    IReadOnlyDictionary<string, long> Snapshot();

    /// <summary>Clears all counters.</summary>
    void Reset();
}
