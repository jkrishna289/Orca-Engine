using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wholphin.Engine.Data;
using Wholphin.Engine.Data.Enums;
using Wholphin.Engine.Diagnostics;

namespace Wholphin.Engine.Analytics;

/// <summary>
/// Default <see cref="ICommunityRatingService"/>. Reads the rating-bearing behavior signals across all
/// users, groups them per catalog item, and writes the Bayesian-averaged Wholphin score + vote count
/// back onto the catalog row.
/// </summary>
public class CommunityRatingService : ICommunityRatingService
{
    private static readonly BehaviorEventType[] RatingSignals =
    {
        BehaviorEventType.Rated, BehaviorEventType.ThumbsUp, BehaviorEventType.ThumbsDown,
        BehaviorEventType.MarkedFavorite, BehaviorEventType.PlaybackCompleted,
    };

    private readonly IWholphinDbContextFactory _factory;
    private readonly IEngineMetrics _metrics;
    private readonly ILogger<CommunityRatingService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CommunityRatingService"/> class.
    /// </summary>
    public CommunityRatingService(IWholphinDbContextFactory factory, IEngineMetrics metrics, ILogger<CommunityRatingService> logger)
    {
        _factory = factory;
        _metrics = metrics;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<int> RecomputeAllAsync(CancellationToken ct = default)
    {
        await using var db = _factory.Create();

        var events = await db.BehaviorEvents
            .Where(e => RatingSignals.Contains(e.EventType))
            .Select(e => new { e.JellyfinItemId, e.CatalogItemId, e.EventType, e.Value })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (events.Count == 0)
        {
            return 0;
        }

        var items = await db.CatalogItems
            .Select(c => new { c.Id, c.JellyfinItemId, c.CommunityRating })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var idSet = items.Select(i => i.Id).ToHashSet();
        var byJellyfin = items
            .Where(i => i.JellyfinItemId != null)
            .GroupBy(i => i.JellyfinItemId!.Value)
            .ToDictionary(g => g.Key, g => g.First().Id);
        var priorById = items.ToDictionary(i => i.Id, i => i.CommunityRating);

        // Group each event's implied (rating, weight) by the catalog item it concerns.
        var signalsByItem = new Dictionary<long, List<(double Rating, double Weight)>>();
        foreach (var e in events)
        {
            var catalogId = e.CatalogItemId is { } cid && idSet.Contains(cid)
                ? cid
                : (e.JellyfinItemId is { } jid && byJellyfin.TryGetValue(jid, out var mapped) ? mapped : (long?)null);
            if (catalogId is null)
            {
                continue;
            }

            if (CommunityRatingMath.Implied(e.EventType, e.Value) is not { } implied)
            {
                continue;
            }

            if (!signalsByItem.TryGetValue(catalogId.Value, out var list))
            {
                list = new List<(double, double)>();
                signalsByItem[catalogId.Value] = list;
            }

            list.Add(implied);
        }

        if (signalsByItem.Count == 0)
        {
            return 0;
        }

        // Update only the affected rows (tracked) so we don't rewrite the whole catalog.
        var affectedIds = signalsByItem.Keys.ToList();
        var rows = await db.CatalogItems
            .Where(c => affectedIds.Contains(c.Id))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var written = 0;
        foreach (var row in rows)
        {
            double? prior = priorById.TryGetValue(row.Id, out var p) && p is { } pr ? pr : null;
            if (CommunityRatingMath.Aggregate(signalsByItem[row.Id], prior) is not { } agg)
            {
                continue;
            }

            row.WholphinRating = agg.Score;
            row.WholphinVotes = agg.Votes;
            written++;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        _metrics.Increment("community_rating.recompute", written);
        _logger.LogInformation("Orca Engine: community ratings recomputed for {Count} items.", written);
        return written;
    }
}
