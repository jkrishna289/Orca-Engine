using System;

namespace Wholphin.Engine.Caching;

/// <summary>
/// Abstraction over the L1 cache. Backed by an in-memory cache at Tier 1;
/// swappable for Redis at Tier 2+ behind this same port.
/// </summary>
public interface ICache
{
    /// <summary>Tries to get a cached value.</summary>
    bool TryGet<T>(string key, out T? value);

    /// <summary>Sets a cached value with an optional TTL.</summary>
    void Set<T>(string key, T value, TimeSpan? ttl = null);

    /// <summary>Removes a cached value.</summary>
    void Remove(string key);
}
