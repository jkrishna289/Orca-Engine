using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Wholphin.Engine.Caching;
using Wholphin.Engine.Catalog;
using Wholphin.Engine.Data;
using Wholphin.Engine.Data.Entities;
using Wholphin.Engine.Data.Enums;
using Wholphin.Engine.Embedding;
using Wholphin.Engine.Integrations.Jellyseerr;
using Wholphin.Engine.Integrations.Tmdb;
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
    private readonly ITmdbClient _tmdb;

    // TMDB's related titles are its "people who watched this also watched" signal — far stronger
    // than shared genres, which is all the catalog-only ranking had (ODDTAXI came back as Tulsa King
    // and Slow Horses). Recommendations outrank Similar, and both start above the local blend.
    private const double TmdbRecommendationScore = 0.95;
    private const double TmdbSimilarScore = 0.8;
    private const double TmdbRankStep = 0.015;

    // A title both TMDB and the local ranking picked is the surest match of all.
    private const double AgreementBonus = 0.05;

    // New requestable rows one More Like This lookup may add to the catalog.
    private const int MaxTmdbImports = 20;

    /// <summary>
    /// Initializes a new instance of the <see cref="SimilarityService"/> class.
    /// </summary>
    /// <param name="factory">The database context factory.</param>
    /// <param name="cache">The L1 cache.</param>
    /// <param name="vectorIndex">The TF-IDF content-vector index.</param>
    /// <param name="llm">The LLM curator (More Like This selection under FeatureLlmCuration; fail-soft).</param>
    /// <param name="tmdb">TMDB, for the source title's own recommendations and similar titles.</param>
    public SimilarityService(IWholphinDbContextFactory factory, ICache cache, IContentVectorIndex vectorIndex, ILlmReRanker llm, ITmdbClient tmdb)
    {
        _factory = factory;
        _cache = cache;
        _vectorIndex = vectorIndex;
        _llm = llm;
        _tmdb = tmdb;
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

        var related = TmdbRelatedAsync(source, ct); // in flight while the local ranking runs

        var candidates = await db.CatalogItems.AsNoTracking()
            .Where(c => c.Id != source.Id && c.Availability != AvailabilityState.Unavailable)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var vectors = await VectorsWithoutWaitingAsync(_vectorIndex, ct).ConfigureAwait(false);

        // Under LLM curation the ranking only prefilters a wider pool; the LLM picks which of those a
        // fan of the source would genuinely reach for (fail-soft to this order).
        var local = Merge(source, Rank(source, candidates, take * 3, vectors), await related.ConfigureAwait(false), take * 3);

        // A first-time curation is a live LLM call (seen at ~17 s), and the details page waits on this
        // row. Give it a short window; past that, serve the embedding order now and let curation finish
        // in the background — its result lands in the LLM cache and here, so the next visit gets it.
        // Not tied to the request token: the work should outlive this request.
        var curation = CurateAsync(source, local, take, cacheKey);
        if (await Task.WhenAny(curation, Task.Delay(CurationWait, ct)).ConfigureAwait(false) == curation)
        {
            return await curation.ConfigureAwait(false);
        }

        IReadOnlyList<SimilarityResult> interim = local.Take(take).ToList();
        _cache.Set(cacheKey, interim, InterimCacheTtl);
        return interim;
    }

    private static readonly TimeSpan CurationWait = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// The content-vector index without waiting on a rebuild. The index expires every few hours and
    /// its rebuild embeds every title added since (More Like This and discovery import dozens), which
    /// held a details page's row for ~18 s. With an index already built, serve that and let the
    /// rebuild finish behind it; only a fresh process, with nothing built yet, has to wait.
    /// </summary>
    /// <param name="index">The content-vector index.</param>
    /// <param name="ct">Cancellation token for the wait, when there is one.</param>
    /// <returns>The current index, or the freshly built one.</returns>
    internal static Task<ContentVectorSnapshot> VectorsWithoutWaitingAsync(IContentVectorIndex index, CancellationToken ct)
    {
        // Not the request token: the rebuild should outlive the request that noticed it was due.
        var rebuild = index.GetAsync(CancellationToken.None);
        return index.Current is { } current ? Task.FromResult(current) : rebuild.WaitAsync(ct);
    }

    // Short, so a visit soon after a timed-out curation still picks up the curated row.
    private static readonly TimeSpan InterimCacheTtl = TimeSpan.FromMinutes(1);

    private async Task<IReadOnlyList<SimilarityResult>> CurateAsync(
        CatalogItem source,
        IReadOnlyList<SimilarityResult> local,
        int take,
        string cacheKey)
    {
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
            CancellationToken.None).ConfigureAwait(false);

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

        var vectors = await VectorsWithoutWaitingAsync(_vectorIndex, ct).ConfigureAwait(false);
        var results = Rank(seed, filtered, take, vectors);
        return new BecauseYouWatched(seed, results);
    }

    /// <summary>
    /// TMDB's recommendations and similar titles for the source, as catalog rows: matched to the
    /// existing row for each (TMDB id, type), or imported as a requestable row so it can be shown,
    /// opened and requested like any other. Empty when the source has no TMDB id or TMDB is off.
    /// </summary>
    private async Task<IReadOnlyList<SimilarityResult>> TmdbRelatedAsync(CatalogItem source, CancellationToken ct)
    {
        if (source.TmdbId is not { } tmdbId || !_tmdb.IsConfigured || source.MediaType is not (MediaType.Movie or MediaType.Series))
        {
            return Array.Empty<SimilarityResult>();
        }

        var recommendations = _tmdb.GetRelatedAsync(tmdbId, source.MediaType, TmdbRelatedKind.Recommendations, ct);
        var similar = _tmdb.GetRelatedAsync(tmdbId, source.MediaType, TmdbRelatedKind.Similar, ct);
        var scored = new Dictionary<(int, MediaType), (DiscoverResult Result, double Score)>();
        void Score(IReadOnlyList<DiscoverResult> results, double top)
        {
            for (var i = 0; i < results.Count; i++)
            {
                var key = (results[i].TmdbId, results[i].MediaType);
                var score = top - (i * TmdbRankStep);
                scored[key] = scored.TryGetValue(key, out var seen)
                    ? (seen.Result, Math.Max(seen.Score, score) + AgreementBonus) // in both lists
                    : (results[i], score);
            }
        }

        Score(await recommendations.ConfigureAwait(false), TmdbRecommendationScore);
        Score(await similar.ConfigureAwait(false), TmdbSimilarScore);
        scored.Remove((tmdbId, source.MediaType));
        if (scored.Count == 0)
        {
            return Array.Empty<SimilarityResult>();
        }

        var ranked = scored.OrderByDescending(kv => kv.Value.Score).ToList();
        await using var db = _factory.Create();
        var rows = await TmdbTitleRows.MatchOrImportAsync(db, ranked.Select(kv => kv.Value.Result).ToList(), MaxTmdbImports, ct)
            .ConfigureAwait(false);

        return ranked
            .Where(kv => rows.TryGetValue(kv.Key, out var item) && item.Availability != AvailabilityState.Unavailable)
            .Select(kv => new SimilarityResult(rows[kv.Key], kv.Value.Score))
            .ToList();
    }

    /// <summary>
    /// Combines the local ranking with TMDB's, one entry per title. The catalog can hold two rows for
    /// the same (TMDB id, type) — a library copy and a TMDB copy, which put Asur in the row twice — so
    /// titles are keyed on that pair and the library row is kept. The source and its own duplicates
    /// are dropped. A title both lists picked gets the agreement bonus.
    /// </summary>
    internal static IReadOnlyList<SimilarityResult> Merge(
        CatalogItem source,
        IReadOnlyList<SimilarityResult> local,
        IReadOnlyList<SimilarityResult> tmdb,
        int take)
    {
        var sourceKey = TitleKey(source);
        Dictionary<string, SimilarityResult> Distinct(IEnumerable<SimilarityResult> results)
        {
            var best = new Dictionary<string, SimilarityResult>();
            foreach (var r in results)
            {
                var key = TitleKey(r.Item);
                if (key == sourceKey || r.Item.Id == source.Id)
                {
                    continue;
                }

                best[key] = best.TryGetValue(key, out var seen)
                    ? new SimilarityResult(Preferred(seen.Item, r.Item), Math.Max(seen.Score, r.Score))
                    : r;
            }

            return best;
        }

        var merged = Distinct(local);
        foreach (var (key, r) in Distinct(tmdb))
        {
            merged[key] = merged.TryGetValue(key, out var seen)
                ? new SimilarityResult(Preferred(seen.Item, r.Item), Math.Max(seen.Score, r.Score) + AgreementBonus)
                : r;
        }

        return merged.Values
            .OrderByDescending(r => r.Score)
            .ThenByDescending(r => r.Item.CommunityRating ?? 0)
            .Take(take)
            .ToList();
    }

    private static string TitleKey(CatalogItem item) =>
        item.TmdbId is { } tmdbId ? $"tmdb:{tmdbId}:{item.MediaType}" : $"id:{item.Id}";

    /// <summary>Of two rows for one title, the one the viewer can actually watch.</summary>
    private static CatalogItem Preferred(CatalogItem a, CatalogItem b) => TmdbTitleRows.Preferred(a, b);

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
