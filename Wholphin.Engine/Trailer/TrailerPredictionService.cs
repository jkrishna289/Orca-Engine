using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wholphin.Engine.Analytics;
using Wholphin.Engine.Behavior;
using Wholphin.Engine.Data;
using Wholphin.Engine.Data.Enums;
using Wholphin.Engine.Diagnostics;
using Wholphin.Engine.Home;

namespace Wholphin.Engine.Trailer;

/// <summary>
/// Default <see cref="ITrailerPredictionService"/>. Combines the signals the engine already logs into
/// one weighted score per title. Deliberately a transparent, deterministic model (every contribution
/// is explainable in the metrics/logs) rather than a black box, and entirely fail-soft: any error
/// falls back to the historical top-community-rating warm list.
/// </summary>
public class TrailerPredictionService : ITrailerPredictionService
{
    // How much each signal family contributes, before per-signal recency/strength scaling.
    private const double ContinueWatchingWeight = 3.0;
    private const double EngagementWeight = 2.0;
    private const double InterestWeight = 1.5;
    private const double PopularityWeight = 1.0;

    private const int RecentEventLimit = 1000;
    private const int MaxContinueWatchingUsers = 5;
    private const int ResumeLimitPerUser = 10;
    private const int MemoryLimit = 200;
    private const int PopularityLimit = 40;

    private readonly IWholphinDbContextFactory _factory;
    private readonly IBehaviorService _behavior;
    private readonly IResumeProvider _resume;
    private readonly IPopularityService _popularity;
    private readonly IEngineMetrics _metrics;
    private readonly ILogger<TrailerPredictionService> _logger;

    /// <summary>Initializes a new instance of the <see cref="TrailerPredictionService"/> class.</summary>
    public TrailerPredictionService(
        IWholphinDbContextFactory factory,
        IBehaviorService behavior,
        IResumeProvider resume,
        IPopularityService popularity,
        IEngineMetrics metrics,
        ILogger<TrailerPredictionService> logger)
    {
        _factory = factory;
        _behavior = behavior;
        _resume = resume;
        _popularity = popularity;
        _metrics = metrics;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<(int TmdbId, MediaType MediaType)>> GetWarmCandidatesAsync(int max, CancellationToken ct = default)
    {
        if (max <= 0)
        {
            return Array.Empty<(int, MediaType)>();
        }

        try
        {
            var scores = new Dictionary<(int TmdbId, MediaType MediaType), double>();
            var now = DateTime.UtcNow;

            void Add(int tmdbId, MediaType mediaType, double weight)
            {
                if (tmdbId <= 0 || weight <= 0 || mediaType is not (MediaType.Movie or MediaType.Series))
                {
                    return;
                }

                var key = (tmdbId, mediaType);
                scores[key] = scores.GetValueOrDefault(key) + weight;
            }

            await using var db = _factory.Create();

            // ── Recent engagement (focus / click / trailer / completion), recency + time-of-day weighted.
            var events = await _behavior.GetRecentAsync(null, RecentEventLimit, ct).ConfigureAwait(false);

            var catalogIds = events.Where(e => e.CatalogItemId is > 0).Select(e => e.CatalogItemId!.Value).Distinct().ToList();
            var jellyfinIds = events.Where(e => e.CatalogItemId is null && e.JellyfinItemId is not null)
                .Select(e => e.JellyfinItemId!.Value).Distinct().ToList();

            var byCatalogId = catalogIds.Count == 0
                ? new Dictionary<long, (int TmdbId, MediaType MediaType)>()
                : await db.CatalogItems
                    .Where(c => catalogIds.Contains(c.Id) && c.TmdbId != null)
                    .Select(c => new { c.Id, TmdbId = c.TmdbId!.Value, c.MediaType })
                    .ToDictionaryAsync(x => x.Id, x => (x.TmdbId, x.MediaType), ct)
                    .ConfigureAwait(false);

            var byJellyfinId = jellyfinIds.Count == 0
                ? new Dictionary<Guid, (int TmdbId, MediaType MediaType)>()
                : await db.CatalogItems
                    .Where(c => c.JellyfinItemId != null && jellyfinIds.Contains(c.JellyfinItemId.Value) && c.TmdbId != null)
                    .Select(c => new { JellyfinId = c.JellyfinItemId!.Value, TmdbId = c.TmdbId!.Value, c.MediaType })
                    .ToDictionaryAsync(x => x.JellyfinId, x => (x.TmdbId, x.MediaType), ct)
                    .ConfigureAwait(false);

            foreach (var ev in events)
            {
                (int TmdbId, MediaType MediaType) key;
                if (ev.CatalogItemId is { } cid && byCatalogId.TryGetValue(cid, out var v1))
                {
                    key = v1;
                }
                else if (ev.JellyfinItemId is { } jid && byJellyfinId.TryGetValue(jid, out var v2))
                {
                    key = v2;
                }
                else
                {
                    continue;
                }

                var typeWeight = EventTypeWeight(ev.EventType);
                if (typeWeight <= 0)
                {
                    continue;
                }

                var ageDays = Math.Max(0, (now - ev.Timestamp).TotalDays);
                var recency = 1.0 / (1.0 + ageDays);
                // Time-of-day affinity: engagement around the current hour is a stronger "now" signal.
                var todBoost = HourDistance(ev.Timestamp.Hour, now.Hour) <= 2 ? 1.3 : 1.0;
                Add(key.TmdbId, key.MediaType, EngagementWeight * typeWeight * recency * todBoost);
            }

            // ── Continue Watching for the most recently active users (strongest intent).
            var recentUsers = events
                .Where(e => e.UserId != Guid.Empty)
                .GroupBy(e => e.UserId)
                .OrderByDescending(g => g.Max(x => x.Timestamp))
                .Take(MaxContinueWatchingUsers)
                .Select(g => g.Key)
                .ToList();
            foreach (var userId in recentUsers)
            {
                var resume = await _resume.GetResumeAsync(userId, ResumeLimitPerUser, ct).ConfigureAwait(false);
                foreach (var rp in resume)
                {
                    if (rp.Item.TmdbId is { } tmdb)
                    {
                        // Mid-watch weighs highest; nearly-finished a bit less (less likely to resume).
                        Add(tmdb, rp.Item.MediaType, ContinueWatchingWeight * (1.0 - (rp.Progress * 0.4)));
                    }
                }
            }

            // ── Longitudinal per-item interest.
            var memories = await db.UserItemMemories
                .OrderByDescending(m => m.UpdatedAt)
                .Take(MemoryLimit)
                .Select(m => new { m.TmdbId, m.MediaType, m.InterestScore })
                .ToListAsync(ct)
                .ConfigureAwait(false);
            foreach (var m in memories)
            {
                Add(m.TmdbId, m.MediaType, InterestWeight * m.InterestScore);
            }

            // ── Popularity leaderboards (watched + completed).
            var popularity = await _popularity.BuildAsync(PopularityLimit, ct).ConfigureAwait(false);
            var popular = popularity.MostWatched.Concat(popularity.MostCompleted).ToList();
            var popCatalogIds = popular.Select(p => p.CatalogId).Distinct().ToList();
            if (popCatalogIds.Count > 0)
            {
                var popMap = await db.CatalogItems
                    .Where(c => popCatalogIds.Contains(c.Id) && c.TmdbId != null)
                    .Select(c => new { c.Id, TmdbId = c.TmdbId!.Value, c.MediaType })
                    .ToDictionaryAsync(x => x.Id, x => (x.TmdbId, x.MediaType), ct)
                    .ConfigureAwait(false);
                foreach (var p in popular)
                {
                    if (popMap.TryGetValue(p.CatalogId, out var v))
                    {
                        Add(v.Item1, v.Item2, PopularityWeight * Math.Log(1 + p.Count));
                    }
                }
            }

            if (scores.Count == 0)
            {
                return await RatingFallbackAsync(db, max, ct).ConfigureAwait(false);
            }

            _metrics.Increment("trailer.prediction.scored", scores.Count);
            return scores
                .OrderByDescending(kv => kv.Value)
                .Take(max)
                .Select(kv => kv.Key)
                .ToList();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Orca Engine: trailer prediction failed; falling back to rating-based warming.");
            _metrics.Increment("trailer.prediction.error");
            try
            {
                await using var db = _factory.Create();
                return await RatingFallbackAsync(db, max, ct).ConfigureAwait(false);
            }
            catch
            {
                return Array.Empty<(int, MediaType)>();
            }
        }
    }

    /// <summary>Cold-start / failure fallback: the historical top-community-rating WatchNow + requestable list.</summary>
    private static async Task<IReadOnlyList<(int TmdbId, MediaType MediaType)>> RatingFallbackAsync(
        WholphinDbContext db, int max, CancellationToken ct)
    {
        var rows = await db.CatalogItems
            .Where(c => c.TmdbId != null
                && (c.Availability == AvailabilityState.WatchNow || c.Availability == AvailabilityState.Request))
            .OrderByDescending(c => c.CommunityRating)
            .Take(max)
            .Select(c => new { TmdbId = c.TmdbId!.Value, c.MediaType })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return rows.Select(r => (r.TmdbId, r.MediaType)).ToList();
    }

    /// <summary>Per-signal strength: explicit/high-intent events outweigh a passing focus.</summary>
    private static double EventTypeWeight(BehaviorEventType type) => type switch
    {
        BehaviorEventType.ThumbsUp => 3.0,
        BehaviorEventType.TrailerPlayed => 3.0,
        BehaviorEventType.PlaybackCompleted => 2.5,
        BehaviorEventType.RequestCreated => 2.5,
        BehaviorEventType.CardClicked => 2.0,
        BehaviorEventType.PlaybackStarted => 1.5,
        BehaviorEventType.CardFocused => 1.0,
        BehaviorEventType.Browsed => 0.5,
        _ => 0.0,
    };

    /// <summary>Circular distance between two hours-of-day (0..12).</summary>
    private static int HourDistance(int a, int b)
    {
        var d = Math.Abs(a - b);
        return Math.Min(d, 24 - d);
    }
}
