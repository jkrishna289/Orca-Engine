using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Wholphin.Engine.Diagnostics;

/// <summary>
/// Default <see cref="IEngineMetrics"/> backed by a concurrent counter map.
/// </summary>
public class EngineMetrics : IEngineMetrics
{
    private readonly ConcurrentDictionary<string, long> _counters = new();

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
    public IReadOnlyDictionary<string, long> Snapshot() =>
        new SortedDictionary<string, long>(_counters);

    /// <inheritdoc />
    public void Reset() => _counters.Clear();
}
