using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Wholphin.Engine.Diagnostics;

/// <summary>
/// Default <see cref="IEngineMetrics"/> backed by a concurrent counter map, with timed operations
/// mirrored into <see cref="IEngineEvents"/> so they get a clock as well as a total.
/// </summary>
public class EngineMetrics : IEngineMetrics
{
    private readonly ConcurrentDictionary<string, long> _counters = new();
    private readonly IEngineEvents _events;

    /// <summary>
    /// Initializes a new instance of the <see cref="EngineMetrics"/> class.
    /// </summary>
    /// <param name="events">The structured event log that timed operations are mirrored into.</param>
    public EngineMetrics(IEngineEvents events) => _events = events;

    /// <inheritdoc />
    public void Increment(string key, long by = 1)
    {
        if (string.IsNullOrEmpty(key) || by == 0)
        {
            return;
        }

        _counters.AddOrUpdate(key, by, (_, current) => current + by);
    }

    /// <inheritdoc />
    public void Record(string key, long elapsedMs, bool ok = true, string? data = null, Exception? exception = null)
    {
        if (string.IsNullOrEmpty(key))
        {
            return;
        }

        Increment($"{key}.count");
        Increment($"{key}.total_ms", elapsedMs);

        // "home.row.foryou" → component "home", event "row.foryou". Splitting on the first segment
        // gives the dashboard its component filter for free, from keys that already exist.
        var dot = key.IndexOf('.', StringComparison.Ordinal);
        var component = dot > 0 ? key[..dot] : key;
        var name = dot > 0 && dot < key.Length - 1 ? key[(dot + 1)..] : key;

        _events.Emit(ok ? "info" : "error", component, name, elapsedMs, data, exception);
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, long> Snapshot() =>
        new SortedDictionary<string, long>(_counters);

    /// <inheritdoc />
    public void Reset() => _counters.Clear();
}
