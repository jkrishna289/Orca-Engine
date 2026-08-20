using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Wholphin.Engine.Caching;

/// <summary>
/// Cache-or-compute with in-flight coalescing: when N callers ask for the same uncached key at the
/// same moment, the factory runs exactly ONCE and everyone awaits that one result.
/// </summary>
/// <remarks>
/// <para>
/// A static helper OVER <see cref="ICache"/> rather than a method ON it, deliberately: coalescing is
/// a property of this process, not of the cache store. Putting it on the port would force the Redis
/// implementation the interface promises at Tier 2 to reimplement it.
/// </para>
/// <para>
/// Positive and negative results share one entry, so a lookup costs one cache probe and produces one
/// hit/miss counter. Only the TTL differs — "this provider has nothing for this title" is worth
/// remembering, but for hours rather than weeks.
/// </para>
/// </remarks>
public static class SingleFlight
{
    // ponytail: process-wide map on a static. Make it an instance service if a second engine ever
    // runs in the same process.
    private static readonly ConcurrentDictionary<string, Lazy<Task<object?>>> InFlight = new(StringComparer.Ordinal);

    /// <summary>
    /// Returns the cached value, or produces and caches it — coalescing concurrent callers.
    /// </summary>
    /// <typeparam name="T">The cached type.</typeparam>
    /// <param name="cache">The L1 cache.</param>
    /// <param name="key">The full cache key; the segment before its first ':' drives the hit/miss counters.</param>
    /// <param name="ttl">How long a non-null result is kept.</param>
    /// <param name="negativeTtl">How long a null result is remembered, so a known miss isn't immediately re-fetched.</param>
    /// <param name="factory">Produces the value on a miss.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The cached or freshly produced value, or null.</returns>
    /// <remarks>
    /// The factory observes the FIRST caller's cancellation token — coalescing means there is only
    /// one call to cancel. Callers here are background enrichers and queue workers, where that is the
    /// wanted behaviour.
    /// </remarks>
    public static async Task<T?> GetOrCreateAsync<T>(
        ICache cache,
        string key,
        TimeSpan ttl,
        TimeSpan negativeTtl,
        Func<CancellationToken, Task<T?>> factory,
        CancellationToken cancellationToken)
        where T : class
    {
        if (cache.TryGet<Entry>(key, out var cached) && cached is not null)
        {
            return cached.Value as T;
        }

        var lazy = InFlight.GetOrAdd(
            key,
            k => new Lazy<Task<object?>>(
                () => ProduceAsync(cache, k, ttl, negativeTtl, factory, cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            return await lazy.Value.ConfigureAwait(false) as T;
        }
        finally
        {
            // The winner's result is already cached by the time anyone gets here, so a caller
            // arriving after this point reads the cache instead of starting a second call.
            InFlight.TryRemove(key, out _);
        }
    }

    private static async Task<object?> ProduceAsync<T>(
        ICache cache,
        string key,
        TimeSpan ttl,
        TimeSpan negativeTtl,
        Func<CancellationToken, Task<T?>> factory,
        CancellationToken cancellationToken)
        where T : class
    {
        var value = await factory(cancellationToken).ConfigureAwait(false);

        // A throwing factory never reaches here: a transient failure is not evidence of absence, so
        // it must not be cached as one.
        cache.Set(key, new Entry { Value = value }, value is null ? negativeTtl : ttl);
        return value;
    }

    /// <summary>Wraps the value so a cached null is a hit rather than indistinguishable from a miss.</summary>
    private sealed class Entry
    {
        public object? Value { get; init; }
    }
}
