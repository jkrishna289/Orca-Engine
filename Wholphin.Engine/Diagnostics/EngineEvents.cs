using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Wholphin.Engine.Diagnostics;

/// <summary>
/// Default <see cref="IEngineEvents"/>: two bounded FIFO rings sharing one sequence counter.
/// </summary>
/// <remarks>
/// Errors live in their own ring so routine traffic can't evict them. With a single ring, a busy
/// server building a home page a second would flush the buffer every few minutes and the failure
/// you came to look at would already be gone — the errors are rare, which is exactly why they
/// survive far longer once separated.
/// </remarks>
public sealed class EngineEvents : IEngineEvents
{
    /// <summary>How many routine events are retained.</summary>
    public const int InfoCapacity = 2000;

    /// <summary>How many errors are retained. Small ring, but errors are rare, so it spans hours.</summary>
    public const int ErrorCapacity = 500;

    private const int MaxDataChars = 512;
    private const int MaxExceptionChars = 4096;

    private readonly ConcurrentQueue<EngineEvent> _info = new();
    private readonly ConcurrentQueue<EngineEvent> _errors = new();
    private long _seq;

    /// <inheritdoc />
    public long LastSeq => Interlocked.Read(ref _seq);

    /// <inheritdoc />
    public void Emit(string level, string component, string @event, long? elapsedMs = null, string? data = null, Exception? exception = null)
    {
        if (string.IsNullOrEmpty(component) || string.IsNullOrEmpty(@event))
        {
            return;
        }

        var isError = !string.Equals(level, "info", StringComparison.Ordinal)
            && !string.Equals(level, "warn", StringComparison.Ordinal);

        var entry = new EngineEvent(
            Interlocked.Increment(ref _seq),
            DateTime.UtcNow,
            level,
            component,
            @event,
            elapsedMs,
            Truncate(data, MaxDataChars),
            Truncate(exception?.ToString(), MaxExceptionChars));

        // An unbounded exception message multiplied by the ring size is memory we'd then serve over
        // HTTP, so both payloads are capped above rather than trusted.
        var ring = isError ? _errors : _info;
        var capacity = isError ? ErrorCapacity : InfoCapacity;
        ring.Enqueue(entry);
        while (ring.Count > capacity && ring.TryDequeue(out _))
        {
            // Trim to capacity. Racing writers may briefly overshoot; harmless.
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<EngineEvent> Recent(int limit = 200, bool errorsOnly = false)
    {
        limit = Math.Clamp(limit, 1, InfoCapacity + ErrorCapacity);
        var source = errorsOnly ? _errors.ToArray() : Merged();
        return source.OrderByDescending(e => e.Seq).Take(limit).ToList();
    }

    /// <inheritdoc />
    public IReadOnlyList<EngineEvent> Since(long seq, int limit = 500)
    {
        limit = Math.Clamp(limit, 1, InfoCapacity + ErrorCapacity);
        return Merged()
            .Where(e => e.Seq > seq)
            .OrderBy(e => e.Seq)
            .Take(limit)
            .ToList();
    }

    /// <summary>
    /// Both rings as one set. Each ring is already ordered by sequence (they're FIFO and the
    /// counter is monotonic), so callers only need to merge, not sort from scratch.
    /// </summary>
    private EngineEvent[] Merged()
    {
        var info = _info.ToArray();
        var errors = _errors.ToArray();
        var merged = new EngineEvent[info.Length + errors.Length];
        info.CopyTo(merged, 0);
        errors.CopyTo(merged, info.Length);
        return merged;
    }

    private static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max] + "…";
}
