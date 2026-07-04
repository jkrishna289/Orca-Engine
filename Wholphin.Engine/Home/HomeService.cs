using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Wholphin.Engine.Caching;
using Wholphin.Engine.Contracts;
using Wholphin.Engine.Data;
using Wholphin.Engine.Diagnostics;
using Wholphin.Engine.Data.Entities;
using Wholphin.Engine.Data.Enums;
using Wholphin.Engine.Llm;
using Wholphin.Engine.Personalization;
using Wholphin.Engine.Presentation;
using Wholphin.Engine.Recommendation;
using Wholphin.Engine.Settings;

namespace Wholphin.Engine.Home;

/// <summary>
/// Builds the home <see cref="RenderBundle"/>. When a user is supplied it leads with a
/// personalized "For You" row from the recommender, followed by content-based discovery rows.
/// Personalized bundles are cached as read-models (precomputed by the recompute worker).
/// </summary>
public class HomeService
{
    /// <summary>Default items-per-row used when precomputing the cached home read-model.</summary>
    public const int DefaultRowSize = 20;

    /// <summary>
    /// Over-fetch factor for the generic library/discovery queries so each row still fills to
    /// <c>size</c> after cross-row de-duplication (<see cref="DeduplicateRows"/>) drops titles a
    /// higher-priority row already claimed.
    /// </summary>
    private const int OverFetchFactor = 3;


    /// <summary>Time a precomputed home read-model stays valid before a rebuild.</summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(15);

    // Card types the Wholphin app advertises today; used to precompute a cache entry that the
    // app's own requests will hit. Mismatched capability sets simply rebuild live (still correct).
    private static readonly CardType[] DefaultClientCardTypes =
    {
        CardType.PosterPortrait, CardType.BannerWide, CardType.Episode, CardType.PersonCircle,
        CardType.Genre, CardType.Studio, CardType.Discover, CardType.Season
    };

    /// <summary>Affinity confidence at/above which the home leads with personalized rows.</summary>
    private const double ConfidenceLayoutThreshold = 0.40;

    private readonly IWholphinDbContextFactory _factory;
    private readonly ICardSelector _cardSelector;
    private readonly IRecommender _recommender;
    private readonly ISimilarityService _similarity;
    private readonly IPersonalizationService _personalization;
    private readonly IResumeProvider _resume;
    private readonly INewSinceProvider _newSince;
    private readonly Catalog.IUpcomingProvider _upcoming;
    private readonly Recommendation.IMoodCollectionService _mood;
    private readonly ICache _cache;
    private readonly ISettingsService _settings;
    private readonly IEngineMetrics _metrics;
    private readonly ILlmReRanker _llmReRanker;
    private readonly Explanation.IExplanationService _explanation;
    private readonly IReadOnlyList<IRowProvider> _rowProviders;

    /// <summary>
    /// Initializes a new instance of the <see cref="HomeService"/> class.
    /// </summary>
    public HomeService(
        IWholphinDbContextFactory factory,
        ICardSelector cardSelector,
        IRecommender recommender,
        ISimilarityService similarity,
        IPersonalizationService personalization,
        IResumeProvider resume,
        INewSinceProvider newSince,
        Catalog.IUpcomingProvider upcoming,
        Recommendation.IMoodCollectionService mood,
        ICache cache,
        ISettingsService settings,
        IEngineMetrics metrics,
        ILlmReRanker llmReRanker,
        Explanation.IExplanationService explanation,
        IEnumerable<IRowProvider> rowProviders)
    {
        _factory = factory;
        _cardSelector = cardSelector;
        _recommender = recommender;
        _similarity = similarity;
        _personalization = personalization;
        _resume = resume;
        _newSince = newSince;
        _upcoming = upcoming;
        _mood = mood;
        _cache = cache;
        _settings = settings;
        _metrics = metrics;
        _llmReRanker = llmReRanker;
        _explanation = explanation;
        _rowProviders = rowProviders.ToList();
    }

    /// <summary>The capability set used to precompute the cached home read-model.</summary>
    /// <returns>The default client capabilities.</returns>
    public static ClientCapabilities DefaultCapabilities() =>
        new() { SupportedCardTypes = DefaultClientCardTypes.ToList() };

    /// <summary>Builds the stable cache key for a user's home read-model.</summary>
    /// <param name="userId">The user id.</param>
    /// <param name="rowSize">Items per row.</param>
    /// <param name="capabilities">The client capabilities.</param>
    /// <returns>The cache key.</returns>
    public static string CacheKey(Guid userId, int rowSize, ClientCapabilities capabilities)
    {
        var caps = capabilities.SupportedCardTypes.Count == 0
            ? "all"
            : string.Join('+', capabilities.SupportedCardTypes.Select(t => (int)t).OrderBy(x => x));

        // Include the daypart so the context-aware "For You" / spotlight don't serve a stale
        // time-of-day flavor; entries for other dayparts simply expire on their own TTL.
        return $"home:{userId:N}:{rowSize}:{caps}:{Daypart.Current()}";
    }

    /// <summary>Builds the home bundle, optionally personalized for a user (cached per user).</summary>
    public async Task<RenderBundle> BuildAsync(ClientCapabilities capabilities, int rowSize, Guid? userId, CancellationToken ct)
    {
        var personalized = userId is { } u && u != Guid.Empty;

        // Layered settings (admin + per-user roaming overrides) gate which rows we emit.
        var settings = await _settings.ResolveAsync(userId, ct).ConfigureAwait(false);
        var flags = settings.Features;
        var size = rowSize is >= 1 and <= 100 ? rowSize : settings.DefaultRowSize;

        string? cacheKey = personalized ? CacheKey(userId!.Value, size, capabilities) : null;
        if (cacheKey is not null && _cache.TryGet<RenderBundle>(cacheKey, out var cached) && cached is not null)
        {
            _metrics.Increment("home.cache_hit");
            return cached;
        }

        var bundle = new RenderBundle { ContractVersion = 1 };

        // Personalized recommendations drive both the spotlight billboard and the "For You" row.
        IReadOnlyList<ResumePoint> resumePoints = Array.Empty<ResumePoint>();
        var confidence = 0.0;
        if (personalized && flags.Personalization)
        {
            var affinity = await _personalization.GetAsync(userId!.Value, ct).ConfigureAwait(false);
            confidence = affinity.Confidence;

            var recs = await _recommender.RecommendAsync(userId!.Value, size, ct).ConfigureAwait(false);
            var recItems = recs.Select(r => r.Item).ToList();

            // Spotlight billboard: the top picks as a rotating set of Hero cards, only when the
            // feature is enabled and the client can render them.
            if (recItems.Count > 0 && flags.Spotlight && capabilities.SupportedCardTypes.Contains(CardType.Hero))
            {
                AddRow(bundle, "spotlight", "Spotlight", "spotlight", recItems.Take(settings.SpotlightCount).ToList(), capabilities, RowStyle.Hero);
            }

            // Opt-in Stage-3: a hosted LLM (Groq) may reorder "For You" + supply a row title + "why"
            // blurbs. Self-gating + fail-soft — returns the local order unchanged when off/unconfigured.
            var reranked = await _llmReRanker.ReRankAsync(userId!.Value, recItems, confidence, ct).ConfigureAwait(false);
            var forYouTitle = string.IsNullOrWhiteSpace(reranked.RowTitle) ? "For You" : reranked.RowTitle!;

            // Every card gets a deterministic reason from the explanation engine; the LLM's richer
            // "why" (when configured + applied) overrides per item. So For You is self-explaining
            // with Groq off, and better with it on.
            var reasons = new Dictionary<long, string>();
            foreach (var item in reranked.Items)
            {
                reasons[item.Id] = _explanation.ExplainRecommendation(item, affinity);
            }

            foreach (var (id, why) in reranked.Reasons)
            {
                reasons[id] = why;
            }

            AddRow(bundle, "foryou", forYouTitle, "recommended", reranked.Items, capabilities, reasons: reasons);

            // "Because You Watched X": seeded by the user's latest watch. The similarity engine
            // prefilters a wide candidate pool; when the LLM is configured it then GENUINELY picks
            // which of those a fan of the seed would watch next (with per-item "why" blurbs shown as
            // card subtitles). Fail-soft: without an LLM the similarity order stands unchanged.
            if (flags.SimilarityRows)
            {
                var byw = await _similarity.BecauseYouWatchedAsync(userId!.Value, size * 2, ct).ConfigureAwait(false);
                if (byw.Seed is { } seed && byw.Items.Count > 0)
                {
                    var candidates = byw.Items.Select(r => r.Item).ToList();
                    var picked = await _llmReRanker.PickForSeedAsync(seed, candidates, ct).ConfigureAwait(false);
                    var title = string.IsNullOrWhiteSpace(seed.Title) ? "Because You Watched" : $"Because You Watched {seed.Title}";
                    AddRow(
                        bundle,
                        "becauseyouwatched",
                        title,
                        "similar",
                        picked.Items.Take(size).ToList(),
                        capabilities,
                        reasons: picked.Applied ? picked.Reasons : null);
                }
            }

            // "New Since You Were Away": catalog items added/aired since the user's last visit.
            if (flags.NewSinceAway)
            {
                var newItems = await _newSince.GetAsync(userId!.Value, size, ct).ConfigureAwait(false);
                AddRow(bundle, "newsince", "New Since You Were Away", "newsince", newItems, capabilities);
            }
        }

        // Continue Watching points are fetched here but the row is added after Recently Added,
        // matching the home layout order (For You → Recently Added → Continue Watching).
        if (personalized && flags.ContinueWatching)
        {
            resumePoints = await _resume.GetResumeAsync(userId!.Value, size, ct).ConfigureAwait(false);
        }

        await using var db = _factory.Create();

        var recentlyAdded = await db.CatalogItems
            .OrderByDescending(c => c.DateAdded)
            .Take(size * OverFetchFactor)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        AddRow(bundle, "recent", "Recently Added", "recent", recentlyAdded, capabilities);

        // Continue Watching: the user's in-progress items, sourced from Jellyfin resume points.
        AddResumeRow(bundle, resumePoints, capabilities);

        // Coming Soon: upcoming episodes/releases for catalog titles (*arr calendar → TMDB fallback).
        if (flags.ComingSoon)
        {
            var upcoming = await _upcoming.GetUpcomingAsync(size, ct).ConfigureAwait(false);
            if (upcoming.Count > 0)
            {
                var items = upcoming.Select(u => u.Item).ToList();
                var labels = upcoming.ToDictionary(u => u.Item.Id, u => u.Label);
                AddRow(bundle, "comingsoon", "Coming Soon", "upcoming", items, capabilities, reasons: labels);
            }
        }

        // Pluggable row providers: the justified discovery rows (trending with a pinch of taste,
        // You Might Like, pulled Because You Watched, the country row) — and any future row —
        // come from IRowProvider registrations, so new rows never require a HomeService change.
        var providerPriorities = new Dictionary<string, (int Warm, int Cold)>(StringComparer.Ordinal);
        var rowContext = new RowContext
        {
            UserId = personalized ? userId : null,
            Settings = settings,
            Confidence = confidence,
            RowSize = size,
        };
        foreach (var provider in _rowProviders)
        {
            foreach (var providerRow in await provider.BuildAsync(rowContext, ct).ConfigureAwait(false))
            {
                providerPriorities[providerRow.Id] = (providerRow.WarmPriority, providerRow.ColdPriority);
                AddRow(bundle, providerRow.Id, providerRow.Title, providerRow.Purpose, providerRow.Items, capabilities, providerRow.RowStyle, providerRow.Reasons);
            }
        }

        // Mood-based collections (Milestone 8): themed rows (Mind Bending, Dark Thrillers, …).
        if (flags.MoodCollections)
        {
            var moods = await _mood.BuildAsync(size, ct).ConfigureAwait(false);
            foreach (var mood in moods)
            {
                AddRow(bundle, $"mood:{mood.Id}", mood.Title, "mood", mood.Items, capabilities);
            }
        }

        var movies = await db.CatalogItems
            .Where(c => c.MediaType == MediaType.Movie)
            .OrderByDescending(c => c.DateAdded)
            .Take(size * OverFetchFactor)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        AddRow(bundle, "movies", "Movies", "library", movies, capabilities);

        var series = await db.CatalogItems
            .Where(c => c.MediaType == MediaType.Series)
            .OrderByDescending(c => c.DateAdded)
            .Take(size * OverFetchFactor)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        AddRow(bundle, "series", "Series", "library", series, capabilities);

        // Confidence-driven layout: cold profiles lead with global/discovery rows, warm profiles
        // lead with personalized rows (spotlight + Continue Watching always stay pinned to the top).
        if (personalized && flags.Personalization)
        {
            ReorderByConfidence(bundle, confidence, providerPriorities);
        }

        // Cross-row de-duplication in final display order: the highest-priority row keeps each title,
        // lower rows drop repeats — so the same movie/show stops appearing in nearly every row. Runs
        // for non-personalized bundles too (there insertion order already is the display order).
        DeduplicateRows(bundle, size);

        if (cacheKey is not null)
        {
            _cache.Set(cacheKey, bundle, CacheTtl);
        }

        _metrics.Increment("home.built");
        return bundle;
    }

    private static void ReorderByConfidence(
        RenderBundle bundle,
        double confidence,
        IReadOnlyDictionary<string, (int Warm, int Cold)> providerPriorities)
    {
        // OrderBy is a stable sort, so rows with equal priority keep their insertion order.
        bundle.Rows = bundle.Rows.OrderBy(r => LayoutPriority(r.Id, confidence, providerPriorities)).ToList();
    }

    /// <summary>
    /// Removes titles that already appear in a higher (earlier) row, walking rows in final display
    /// order, and caps each de-duplicated row at <paramref name="maxPerRow"/>. The billboard is a
    /// separate surface (left fully independent — its hero may also head "For You"); the numbered
    /// Top-10 keeps all its items (removing any would leave gaps like 1, 2, 4) but still reserves its
    /// titles so the generic rows below don't repeat the most-popular ones. Rows emptied out are dropped.
    /// </summary>
    private static void DeduplicateRows(RenderBundle bundle, int maxPerRow)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in bundle.Rows)
        {
            if (row.Id == "spotlight")
            {
                continue;
            }

            if (row.RowStyle == RowStyle.Top10)
            {
                foreach (var it in row.Items)
                {
                    seen.Add(MediaKey(it));
                }

                continue;
            }

            var kept = new List<RenderItem>(Math.Min(row.Items.Count, maxPerRow));
            foreach (var it in row.Items)
            {
                if (kept.Count >= maxPerRow)
                {
                    break;
                }

                if (seen.Add(MediaKey(it)))
                {
                    kept.Add(it);
                }
            }

            row.Items = kept;
        }

        bundle.Rows = bundle.Rows.Where(r => r.Items.Count > 0).ToList();
    }

    /// <summary>A stable per-title key for de-dup: Jellyfin id when in the library, else the TMDB id.</summary>
    private static string MediaKey(RenderItem item)
    {
        var m = item.Media;
        if (m.JellyfinId is { } jf && jf != Guid.Empty)
        {
            return $"jf:{jf:N}";
        }

        if (m.TmdbId is { } tmdb && tmdb > 0)
        {
            return $"tmdb:{tmdb}:{(int)m.MediaType}";
        }

        // No stable id → treat as unique so it's never wrongly de-duplicated away.
        return $"anon:{Guid.NewGuid():N}";
    }

    private static int LayoutPriority(
        string rowId,
        double confidence,
        IReadOnlyDictionary<string, (int Warm, int Cold)> providerPriorities)
    {
        // Always pinned to the top regardless of confidence.
        switch (rowId)
        {
            case "spotlight": return 0;
            case "continue": return 1;
            case "newsince": return 2; // new content is always worth surfacing early
        }

        // Provider-supplied rows carry their own warm/cold priorities.
        if (providerPriorities.TryGetValue(rowId, out var priority))
        {
            return confidence >= ConfidenceLayoutThreshold ? priority.Warm : priority.Cold;
        }

        // Mood collections sit together as a themed block, just above the library rows.
        if (rowId.StartsWith("mood:", StringComparison.Ordinal))
        {
            return confidence >= ConfidenceLayoutThreshold ? 11 : 12;
        }

        // All requestable/discovery rows (id "discover" or "discover:*") sit together as one block.
        if (rowId.StartsWith("discover", StringComparison.Ordinal))
        {
            return confidence >= ConfidenceLayoutThreshold ? 8 : 5;
        }

        var warm = confidence >= ConfidenceLayoutThreshold;
        return rowId switch
        {
            "foryou" => warm ? 3 : 8,
            "becauseyouwatched" => warm ? 4 : 9,
            "comingsoon" => warm ? 5 : 6,
            "recent" => warm ? 6 : 4,
            "trending" => warm ? 7 : 3,
            "movies" => warm ? 9 : 13,
            "series" => warm ? 10 : 14,
            _ => 100,
        };
    }

    private void AddRow(
        RenderBundle bundle,
        string id,
        string title,
        string purpose,
        IReadOnlyList<CatalogItem> items,
        ClientCapabilities capabilities,
        RowStyle rowStyle = RowStyle.Standard,
        IReadOnlyDictionary<long, string>? reasons = null)
    {
        if (items.Count == 0)
        {
            return;
        }

        var row = new RenderRow { Id = id, Title = title, RowStyle = rowStyle };
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var card = _cardSelector.Select(item, new CardSelectionContext
            {
                RowPurpose = purpose,
                RowStyle = rowStyle,
                RankInRow = i,
                HasResume = false,
                Capabilities = capabilities,
            });

            // Surface the LLM's "why recommended" blurb as the card subtitle when present.
            if (reasons is not null && reasons.TryGetValue(item.Id, out var why) && !string.IsNullOrWhiteSpace(why))
            {
                card.Subtitle = why;
            }

            row.Items.Add(new RenderItem { Media = MediaIdMapper.ToMediaId(item), Card = card });
        }

        bundle.Rows.Add(row);
    }

    private void AddResumeRow(RenderBundle bundle, IReadOnlyList<ResumePoint> points, ClientCapabilities capabilities)
    {
        if (points.Count == 0)
        {
            return;
        }

        var row = new RenderRow { Id = "continue", Title = "Continue Watching", RowStyle = RowStyle.Standard };
        for (var i = 0; i < points.Count; i++)
        {
            var point = points[i];
            var card = _cardSelector.Select(point.Item, new CardSelectionContext
            {
                RowPurpose = "continue",
                RowStyle = RowStyle.Standard,
                RankInRow = i,
                HasResume = true,
                Capabilities = capabilities,
            });

            // Carry the per-item resume position (the selector only knows that a resume exists, not how far).
            card.ShowProgress = true;
            card.Progress = point.Progress;
            if (point.Subtitle is not null)
            {
                card.Subtitle = point.Subtitle;
            }

            row.Items.Add(new RenderItem { Media = MediaIdMapper.ToMediaId(point.Item), Card = card });
        }

        bundle.Rows.Add(row);
    }
}
