using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Wholphin.Engine.Caching;
using Wholphin.Engine.Data;
using Wholphin.Engine.Data.Entities;
using Wholphin.Engine.Data.Enums;
using Wholphin.Engine.Embedding;
using Wholphin.Engine.Llm;

namespace Wholphin.Engine.Recommendation;

/// <summary>
/// On-demand, cached <see cref="ISimilarityService"/>. Scores the source against every non-blacklisted
/// catalog item with <see cref="SimilarityScorer"/>, ranks, and caches the result. Suitable for the
/// self-hosted catalog scale (linear per query); a precomputed/persisted graph is a later optimization
/// once an EF migration strategy is in place.
/// </summary>
public class SimilarityService : ISimilarityService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);

    // How the final score blends flat set-overlap (genres/tags/studios/people, plus media-type/era/
    // rating) with content-vector cosine from the active embedding provider (TF-IDF by default, or a
    // neural model when configured). Weights sum to 1.
    private const double StructuredWeight = 0.65;
    private const double ContentWeight = 0.35;

    // A title with no structured overlap is only admitted when its thematic (overview) cosine is
    // strong enough to stand on its own — surfacing "similar plot, different tags" without re-opening
    // the door to noise that the structured-overlap gate in SimilarityScorer guards against.
    private const double ContentOnlyFloor = 0.15;

    private static readonly TimeSpan LlmSimilarCacheTtl = TimeSpan.FromDays(7);

    private readonly IWholphinDbContextFactory _factory;
    private readonly ICache _cache;
    private readonly IContentVectorIndex _vectorIndex;
    private readonly ILlmReRanker _llm;

    /// <summary>
    /// Initializes a new instance of the <see cref="SimilarityService"/> class.
    /// </summary>
    /// <param name="factory">The database context factory.</param>
    /// <param name="cache">The L1 cache.</param>
    /// <param name="vectorIndex">The TF-IDF content-vector index.</param>
    /// <param name="llm">The LLM curator (More Like This selection under FeatureLlmCuration; fail-soft).</param>
    public SimilarityService(IWholphinDbContextFactory factory, ICache cache, IContentVectorIndex vectorIndex, ILlmReRanker llm)
    {
        _factory = factory;
        _cache = cache;
        _vectorIndex = vectorIndex;
        _llm = llm;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SimilarityResult>> MoreLikeThisAsync(long catalogItemId, int limit, CancellationToken ct = default)
    {
        var take = Math.Clamp(limit, 1, 100);
        var cacheKey = $"sim:{catalogItemId}:{take}";
        if (_cache.TryGet<IReadOnlyList<SimilarityResult>>(cacheKey, out var cached) && cached is not null)
        {
            return cached;
        }

        await using var db = _factory.Create();
        var source = await db.CatalogItems.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == catalogItemId, ct)
            .ConfigureAwait(false);
        if (source is null)
        {
            return Array.Empty<SimilarityResult>();
        }

        var candidates = await db.CatalogItems.AsNoTracking()
            .Where(c => c.Id != source.Id && c.Availability != AvailabilityState.Unavailable)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var vectors = await _vectorIndex.GetAsync(ct).ConfigureAwait(false);

        // Under LLM curation the embedding ranking only prefilters a wider pool; the LLM picks
        // which of those a fan of the source would genuinely reach for (fail-soft to this order).
        var local = Rank(source, candidates, take * 3, vectors);
        var curated = await _llm.CurateAsync(
            new LlmCurationRequest
            {
                Purpose = "similar",
                Pool = local.Select(r => r.Item).ToList(),
                Count = take,
                Seed = source,
                Context = "a 'More Like This' row on the title's detail page — same tone, themes, creators, or story appeal",
                CacheTtl = LlmSimilarCacheTtl,
            },
            ct).ConfigureAwait(false);

        var byId = local.ToDictionary(r => r.Item.Id);
        var results = (IReadOnlyList<SimilarityResult>)curated.Items
            .Where(i => byId.ContainsKey(i.Id))
            .Take(take)
            .Select(i => byId[i.Id])
            .ToList();
        _cache.Set(cacheKey, results, CacheTtl);
        return results;
    }

    /// <inheritdoc />
    public async Task<BecauseYouWatched> BecauseYouWatchedAsync(Guid userId, int limit, CancellationToken ct = default)
    {
        var take = Math.Clamp(limit, 1, 100);

        await using var db = _factory.Create();

        // Seed = the user's most recently watched (completed/marked-played) title.
        var watchedIds = await db.BehaviorEvents.AsNoTracking()
            .Where(e => e.UserId == userId
                        && e.JellyfinItemId != null
                        && (e.EventType == BehaviorEventType.PlaybackCompleted || e.EventType == BehaviorEventType.MarkedPlayed))
            .OrderByDescending(e => e.Timestamp)
            .Select(e => e.JellyfinItemId!.Value)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (watchedIds.Count == 0)
        {
            return new BecauseYouWatched(null, Array.Empty<SimilarityResult>());
        }

        var seedJellyfinId = watchedIds[0];
        var seed = await db.CatalogItems.AsNoTracking()
            .FirstOrDefaultAsync(c => c.JellyfinItemId == seedJellyfinId, ct)
            .ConfigureAwait(false);
        if (seed is null)
        {
            return new BecauseYouWatched(null, Array.Empty<SimilarityResult>());
        }

        // Don't recommend titles the user has already watched.
        var watchedSet = watchedIds.ToHashSet();

        var candidates = await db.CatalogItems.AsNoTracking()
            .Where(c => c.Id != seed.Id && c.Availability != AvailabilityState.Unavailable)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var filtered = candidates
            .Where(c => !(c.JellyfinItemId is { } jid && watchedSet.Contains(jid)));

        var vectors = await _vectorIndex.GetAsync(ct).ConfigureAwait(false);
        var results = Rank(seed, filtered, take, vectors);
        return new BecauseYouWatched(seed, results);
    }

    private static IReadOnlyList<SimilarityResult> Rank(
        CatalogItem source,
        IEnumerable<CatalogItem> candidates,
        int take,
        ContentVectorSnapshot vectors)
    {
        var sourceVector = vectors.VectorFor(source.Id);

        return candidates
            .Select(c =>
            {
                var structured = SimilarityScorer.Score(source, c);
                var content = ContentVector.Cosine(sourceVector, vectors.VectorFor(c.Id));
                var blended = (StructuredWeight * structured) + (ContentWeight * content);
                return (Item: c, Structured: structured, Content: content, Score: blended);
            })
            // Keep structured matches (the SimilarityScorer gate), plus strong thematic-only matches
            // (no shared genre/tag/studio/person but a high overview cosine).
            .Where(r => r.Structured > 0 || r.Content >= ContentOnlyFloor)
            .Select(r => new SimilarityResult(r.Item, r.Score))
            .OrderByDescending(r => r.Score)
            .ThenByDescending(r => r.Item.CommunityRating ?? 0)
            .Take(take)
            .ToList();
    }
}
