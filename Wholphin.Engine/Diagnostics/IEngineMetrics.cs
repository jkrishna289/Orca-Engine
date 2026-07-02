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

    /// <summary>Returns a point-in-time copy of all counters (sorted by key).</summary>
    /// <returns>The counter snapshot.</returns>
    IReadOnlyDictionary<string, long> Snapshot();

    /// <summary>Clears all counters.</summary>
    void Reset();
}
