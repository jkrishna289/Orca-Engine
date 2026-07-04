using System;
using Microsoft.Extensions.Caching.Memory;

namespace Wholphin.Engine.Caching;

/// <summary>
/// Tier-1 in-process cache backed by <see cref="IMemoryCache"/>. Every entry counts as size 1 so
/// the shared cache's <c>SizeLimit</c> bounds the entry count (and thus memory) — the oldest/least-
/// used entries are evicted under pressure instead of growing without bound.
/// </summary>
public class InMemoryCache : ICache
{
    private const int EntrySize = 1;

    private readonly IMemoryCache _cache;

    /// <summary>
    /// Initializes a new instance of the <see cref="InMemoryCache"/> class.
    /// </summary>
    public InMemoryCache(IMemoryCache cache) => _cache = cache;

    /// <inheritdoc />
    public bool TryGet<T>(string key, out T? value)
    {
        if (_cache.TryGetValue(key, out var obj) && obj is T typed)
        {
            value = typed;
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
}
