using System;
using Microsoft.Extensions.Caching.Memory;
using Wholphin.Engine.Diagnostics;

namespace Wholphin.Engine.Caching;

/// <summary>
/// Tier-1 in-process cache. Every entry counts as size 1, so <see cref="EntryLimit"/> bounds the
/// entry count (and thus memory) — the least-used entries are evicted under pressure instead of
/// growing without bound.
/// </summary>
/// <remarks>
/// The cache is PRIVATE to the engine, deliberately. This used to inject Jellyfin's shared
/// <see cref="IMemoryCache"/> and set <c>SizeLimit</c> on it from the plugin's service registrator —
/// but Jellyfin registers that singleton first, so the limit landed on the SERVER-WIDE cache, and
/// <c>MemoryCache</c> throws on any entry without a <c>Size</c> once a limit is set. Jellyfin core
/// caches without one (its own TMDB client, the provider image proxy), so the plugin was arming a
/// crash in unrelated server code.
/// </remarks>
public class InMemoryCache : ICache, IDisposable
{
    /// <summary>The maximum number of entries held before eviction kicks in.</summary>
    public const int EntryLimit = 4096;

    private const int EntrySize = 1;

    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = EntryLimit });
    private readonly IEngineMetrics _metrics;

    /// <summary>
    /// Initializes a new instance of the <see cref="InMemoryCache"/> class.
    /// </summary>
    /// <param name="metrics">Counter sink for the hit/miss rate.</param>
    public InMemoryCache(IEngineMetrics metrics) => _metrics = metrics;

    /// <inheritdoc />
    public bool TryGet<T>(string key, out T? value)
    {
        var hit = _cache.TryGetValue(key, out var obj) && obj is T;
        _metrics.Increment($"cache.{Namespace(key)}.{(hit ? "hit" : "miss")}");

        if (hit)
        {
            value = (T)obj!;
            return true;
        }

        value = default;
        return false;
    }

    /// <inheritdoc />
    public void Set<T>(string key, T value, TimeSpan? ttl = null)
    {
        var options = new MemoryCacheEntryOptions { Size = EntrySize };
        if (ttl.HasValue)
        {
            options.AbsoluteExpirationRelativeToNow = ttl;
        }

        _cache.Set(key, value!, options);
    }

    /// <inheritdoc />
    public void Remove(string key) => _cache.Remove(key);

    /// <summary>
    /// The part of a cache key before the first ':' — "home", "similar", "trivia".
    /// </summary>
    /// <param name="key">The full cache key.</param>
    /// <returns>The namespace segment.</returns>
    /// <remarks>
    /// Counters are keyed on this and NEVER on the full key: keys embed user and item ids, so
    /// per-key counters would grow the metric dictionary without bound — and that dictionary is
    /// copied on every dashboard poll.
    /// </remarks>
    private static string Namespace(string key)
    {
        var colon = key.IndexOf(':', StringComparison.Ordinal);
        return colon > 0 ? key[..colon] : "other";
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _cache.Dispose();
        GC.SuppressFinalize(this);
    }
}
