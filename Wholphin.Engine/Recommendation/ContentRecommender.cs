using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Wholphin.Engine.Data;
using Wholphin.Engine.Data.Entities;
using Wholphin.Engine.Data.Enums;
using Wholphin.Engine.Personalization;

namespace Wholphin.Engine.Recommendation;

/// <summary>
/// Content-first hybrid recommender. Scores the catalog against a user's affinity vector,
/// blending toward global quality priors when confidence is low, with an availability premium,
/// then re-ranks for genre diversity. Collaborative filtering is intentionally omitted —
/// self-hosted servers have too few users for it to work (deferred to a later tier).
/// </summary>
public class ContentRecommender : IRecommender
{
    // Blueprint scoring weights.
    private const double WPersonalization = 0.60;
    private const double WQuality = 0.15;
    private const double WRecency = 0.05;
    private const double WAvailability = 0.10;
    // WTrending = 0.10 is reserved; no trending signal exists in v1.

    private const double DiversityPenalty = 0.15;

    // Exploration (Feature K): reserve ~10% of slots for novelty, but only for warm profiles
    // (cold-start lists are already prior-driven and broad, so exploration would just add noise).
    private const double ExplorationFraction = 0.10;
    private const double ExplorationMinConfidence = 0.40;

    private readonly IWholphinDbContextFactory _factory;
    private readonly IPersonalizationService _personalization;
    private readonly Diagnostics.IEngineMetrics _metrics;
    private readonly Settings.ISettingsService _settings;

    /// <summary>
    /// Initializes a new instance of the <see cref="ContentRecommender"/> class.
    /// </summary>
    /// <param name="factory">Database context factory.</param>
    /// <param name="personalization">Personalization service.</param>
    /// <param name="metrics">Operational metrics.</param>
    /// <param name="settings">Layered settings (gates exploration).</param>
    public ContentRecommender(
        IWholphinDbContextFactory factory,
        IPersonalizationService personalization,
        Diagnostics.IEngineMetrics metrics,
        Settings.ISettingsService settings)
    {
        _factory = factory;
        _personalization = personalization;
        _metrics = metrics;
        _settings = settings;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RecommendationResult>> RecommendAsync(Guid userId, int limit, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 200);
        _metrics.Increment("recommend.calls");

        // Context- and session-aware (Features C + D): the daypart-adjusted long-term taste pulled
        // toward what the user is watching right now.
        var affinity = await _personalization.GetEffectiveAsync(userId, null, ct).ConfigureAwait(false);

        await using var db = _factory.Create();

        // Exclusions: items the user already finished, or explicitly disliked.
        var userEvents = await db.BehaviorEvents.AsNoTracking()
            .Where(e => e.UserId == userId && e.JellyfinItemId.HasValue)
            .Select(e => new { e.JellyfinItemId, e.EventType })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var watched = userEvents
            .Where(e => e.EventType is BehaviorEventType.PlaybackCompleted or BehaviorEventType.MarkedPlayed)
            .Select(e => e.JellyfinItemId!.Value)
            .ToHashSet();

        var disliked = userEvents
            .Where(e => e.EventType == BehaviorEventType.ThumbsDown)
            .Select(e => e.JellyfinItemId!.Value)
            .ToHashSet();

        // Candidate generation: everything not hard-unavailable, minus exclusions.
        var candidates = await db.CatalogItems.AsNoTracking()
            .Where(c => c.Availability != AvailabilityState.Unavailable)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        candidates = candidates
            .Where(c => !c.JellyfinItemId.HasValue ||
                        (!watched.Contains(c.JellyfinItemId.Value) && !disliked.Contains(c.JellyfinItemId.Value)))
            .ToList();

        if (candidates.Count == 0)
        {
            return Array.Empty<RecommendationResult>();
        }

        // Raw personalization scores, then min-max normalize across the candidate set.
        var raw = candidates.ToDictionary(c => c.Id, c => CatalogFeatures.Dot(affinity, c));
        var min = raw.Values.Min();
        var max = raw.Values.Max();
        var span = max - min;

        var now = DateTime.UtcNow;
        var scored = new List<RecommendationResult>(candidates.Count);
        foreach (var c in candidates)
        {
            var personalization = span > 0 ? (raw[c.Id] - min) / span : 0.0;
            var quality = Math.Clamp((c.CommunityRating ?? 0) / 10.0, 0, 1);
            var recency = Recency(c.DateAdded, now);
            var availability = c.Availability == AvailabilityState.WatchNow ? 1.0 : 0.5;

            // Confidence blend: lean on global quality when we barely know the user.
            var personalized = (affinity.Confidence * personalization) + ((1 - affinity.Confidence) * quality);

            var final = (WPersonalization * personalized)
                + (WQuality * quality)
                + (WRecency * recency)
                + (WAvailability * availability);

            scored.Add(new RecommendationResult(c)
            {
                Score = final,
                Personalization = Math.Round(personalization, 4),
                Quality = Math.Round(quality, 4),
                Recency = Math.Round(recency, 4),
                Availability = availability
            });
        }

        var ranked = DiversityRerank(scored, limit);

        // Exploration (Feature K): for warm profiles, swap the weakest tail picks for novel,
        // unfamiliar-genre titles — controlled serendipity that avoids a filter bubble.
        var settings = await _settings.ResolveAsync(userId, ct).ConfigureAwait(false);
        if (settings.Features.Exploration && affinity.Confidence >= ExplorationMinConfidence)
        {
            ranked = InjectExploration(ranked, scored, affinity, limit);
        }

        return ranked;
    }

    /// <summary>
    /// Replaces the weakest tail of the exploit list with high-quality "novel" picks — items from
    /// genres the user has not formed a positive opinion on (never ones they dislike), with one pick
    /// per novel genre for diversity. Preserves the strongest recommendations at the top.
    /// </summary>
    private List<RecommendationResult> InjectExploration(
        List<RecommendationResult> ranked,
        List<RecommendationResult> allScored,
        AffinityVector affinity,
        int limit)
    {
        var exploreCount = (int)Math.Floor(limit * ExplorationFraction);
        if (exploreCount <= 0)
        {
            return ranked;
        }

        var selectedIds = ranked.Select(r => r.Item.Id).ToHashSet();
        var novel = allScored
            .Where(r => !selectedIds.Contains(r.Item.Id))
            .Select(r => (Result: r, Novelty: ExplorationScorer.Novelty(r.Item, affinity)))
            .Where(x => x.Novelty > 0)
            .OrderByDescending(x => x.Novelty)
            .ToList();
        if (novel.Count == 0)
        {
            return ranked;
        }

        var picks = new List<RecommendationResult>();
        var usedGenres = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (result, _) in novel)
        {
            var primary = PrimaryGenre(result.Item);
            if (primary is not null && !usedGenres.Add(primary))
            {
                continue; // one pick per novel genre
            }

            picks.Add(result);
            if (picks.Count >= exploreCount)
            {
                break;
            }
        }

        if (picks.Count == 0)
        {
            return ranked;
        }

        var keep = Math.Max(0, ranked.Count - picks.Count);
        var blended = ranked.Take(keep).ToList();
        blended.AddRange(picks);
        _metrics.Increment("recommend.exploration", picks.Count);
        return blended;
    }

    private static double Recency(DateTime? dateAdded, DateTime now)
    {
        if (dateAdded is not { } added)
        {
            return 0;
        }

        var ageDays = (now - added).TotalDays;
        return Math.Clamp(1 - (ageDays / 365.0), 0, 1);
    }

    /// <summary>
    /// Greedy MMR-lite: repeatedly pick the highest-scoring item, applying a penalty for each
    /// already-selected item sharing its primary genre so one genre can't dominate the result.
    /// </summary>
    private static List<RecommendationResult> DiversityRerank(List<RecommendationResult> scored, int limit)
    {
        var remaining = scored.OrderByDescending(r => r.Score).ToList();
        var selected = new List<RecommendationResult>(Math.Min(limit, remaining.Count));
        var genreCounts = new Dictionary<string, int>();

        while (selected.Count < limit && remaining.Count > 0)
        {
            RecommendationResult? best = null;
            var bestAdjusted = double.NegativeInfinity;

            foreach (var r in remaining)
            {
                var primary = PrimaryGenre(r.Item);
                var penalty = primary is not null && genreCounts.TryGetValue(primary, out var n)
                    ? DiversityPenalty * n
                    : 0;
                var adjusted = r.Score - penalty;
                if (adjusted > bestAdjusted)
                {
                    bestAdjusted = adjusted;
                    best = r;
                }
            }

            if (best is null)
            {
                break;
            }

            selected.Add(best);
            remaining.Remove(best);

            var bestPrimary = PrimaryGenre(best.Item);
            if (bestPrimary is not null)
            {
                genreCounts[bestPrimary] = genreCounts.GetValueOrDefault(bestPrimary) + 1;
            }
        }

        return selected;
    }

    private static string? PrimaryGenre(CatalogItem item)
    {
        var genres = CatalogFeatures.Parse(item.GenresJson);
        return genres.Count > 0 ? genres[0] : null;
    }
}
