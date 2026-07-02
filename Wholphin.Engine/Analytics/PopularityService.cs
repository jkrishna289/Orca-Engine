using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Wholphin.Engine.Caching;
using Wholphin.Engine.Data;
using Wholphin.Engine.Data.Enums;

namespace Wholphin.Engine.Analytics;

/// <summary>
/// Default <see cref="IPopularityService"/>. Counts playback/request signals per catalog item and ranks
/// them into leaderboards. Item identity is resolved the same way community ratings do (catalog id, or
/// the Jellyfin id mapped to a catalog row). Cached for two minutes.
/// </summary>
public class PopularityService : IPopularityService
{
    private const string CacheKeyPrefix = "popularity";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(2);

    private static readonly BehaviorEventType[] Relevant =
    {
        BehaviorEventType.PlaybackStarted, BehaviorEventType.PlaybackCompleted,
        BehaviorEventType.PlaybackStopped, BehaviorEventType.RequestCreated,
    };

    private readonly IWholphinDbContextFactory _factory;
    private readonly ICache _cache;

    /// <summary>
    /// Initializes a new instance of the <see cref="PopularityService"/> class.
    /// </summary>
    public PopularityService(IWholphinDbContextFactory factory, ICache cache)
    {
        _factory = factory;
        _cache = cache;
    }

    /// <inheritdoc />
    public async Task<PopularityReport> BuildAsync(int limit, CancellationToken ct = default)
    {
        var cap = Math.Clamp(limit, 1, 100);
        var cacheKey = $"{CacheKeyPrefix}:{cap}";
        if (_cache.TryGet<PopularityReport>(cacheKey, out var cached) && cached is not null)
        {
            return cached;
        }

        await using var db = _factory.Create();

        var events = await db.BehaviorEvents
            .Where(e => Relevant.Contains(e.EventType))
            .Select(e => new { e.JellyfinItemId, e.CatalogItemId, e.EventType })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var items = await db.CatalogItems
            .Select(c => new { c.Id, c.JellyfinItemId, c.Title })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var idSet = items.Select(i => i.Id).ToHashSet();
        var byJellyfin = items
            .Where(i => i.JellyfinItemId != null)
            .GroupBy(i => i.JellyfinItemId!.Value)
            .ToDictionary(g => g.Key, g => g.First().Id);
        var titleById = items.ToDictionary(i => i.Id, i => i.Title);

        // counts[catalogId][eventType] = n
        var started = new Dictionary<long, int>();
        var completed = new Dictionary<long, int>();
        var stopped = new Dictionary<long, int>();
        var requested = new Dictionary<long, int>();

        foreach (var e in events)
        {
            var catalogId = e.CatalogItemId is { } cid && idSet.Contains(cid)
                ? cid
                : (e.JellyfinItemId is { } jid && byJellyfin.TryGetValue(jid, out var mapped) ? mapped : (long?)null);
            if (catalogId is null)
            {
                continue;
            }

            var bucket = e.EventType switch
            {
                BehaviorEventType.PlaybackStarted => started,
                BehaviorEventType.PlaybackCompleted => completed,
                BehaviorEventType.PlaybackStopped => stopped,
                BehaviorEventType.RequestCreated => requested,
                _ => null,
            };
            if (bucket is null)
            {
                continue;
            }

            bucket[catalogId.Value] = bucket.GetValueOrDefault(catalogId.Value) + 1;
        }

        var report = new PopularityReport
        {
            MostWatched = Rank(started, titleById, cap),
            MostCompleted = Rank(completed, titleById, cap),
            MostRequested = Rank(requested, titleById, cap),
            MostDropped = Rank(stopped, titleById, cap),
            // Rewatched = completed more than once (comfort watches).
            MostRewatched = Rank(completed.Where(kv => kv.Value > 1).ToDictionary(kv => kv.Key, kv => kv.Value), titleById, cap),
        };

        _cache.Set(cacheKey, report, CacheTtl);
        return report;
    }

    private static List<PopularItem> Rank(Dictionary<long, int> counts, Dictionary<long, string> titles, int cap)
        => counts
            .OrderByDescending(kv => kv.Value)
            .Take(cap)
            .Select(kv => new PopularItem(kv.Key, titles.GetValueOrDefault(kv.Key, string.Empty), kv.Value))
            .ToList();
}
