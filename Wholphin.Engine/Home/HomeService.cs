using System;
using System.Collections.Concurrent;
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


    /// <summary>
    /// Age past which a cached home is still SERVED but refreshed in the background, so a viewer
    /// never pays for a rebuild they could have been shown a few-minutes-old page instead.
    /// </summary>
    private static readonly TimeSpan SoftCacheTtl = TimeSpan.FromMinutes(5);

    /// <summary>How long a cached home survives at all. Past this, a request builds inline.</summary>
    private static readonly TimeSpan HardCacheTtl = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Per-row time budgets. Rows are built concurrently, so the home costs the SLOWEST row rather
    /// than the sum of all of them; a row that blows its budget is dropped from this build (and
    /// counted in <c>home.row.*.timeout</c>) instead of stalling or failing the page. Tune from
    /// those counters — a row that times out often is either mis-bucketed or genuinely broken.
    /// </summary>
    private static readonly TimeSpan FastRowBudget = TimeSpan.FromSeconds(3);

    /// <summary>Budget for rows that hit an external service or a provider pipeline.</summary>
    private static readonly TimeSpan MediumRowBudget = TimeSpan.FromSeconds(6);

    /// <summary>Budget for the recommender-backed personalized rows (For You, Because You Watched).</summary>
    private static readonly TimeSpan ExpensiveRowBudget = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Budget for the LLM step INSIDE an expensive row. Curation is pure polish on a row we can
    /// already ship — <see cref="ILlmReRanker"/> is a fail-soft passthrough — so a slow Groq call
    /// costs the row its curation, never the row itself. Kept well under
    /// <see cref="ExpensiveRowBudget"/> so the local ordering still has time to be rendered.
    /// </summary>
    private static readonly TimeSpan LlmStepBudget = TimeSpan.FromSeconds(5);

    // Card types the Wholphin app advertises today; used to precompute a cache entry that the
    // app's own requests will hit. Mismatched capability sets simply rebuild live (still correct).
    private static readonly CardType[] DefaultClientCardTypes =
    {
        CardType.PosterPortrait, CardType.BannerWide, CardType.Episode, CardType.PersonCircle,
        CardType.Genre, CardType.Studio, CardType.Discover, CardType.Season
    };

    /// <summary>Affinity confidence at/above which the home leads with personalized rows.</summary>
    private const double ConfidenceLayoutThreshold = 0.40;

    /// <summary>
    /// Row ids for the cinematic Spotlight showcases. Fixed and unique: the client renders rows
    /// keyed by id, so two showcases sharing one would break the home. The array length also caps
    /// how many a single bundle can carry.
    /// </summary>
    private static readonly string[] SpotlightShowcaseIds =
    {
        "spotlight_feature_1",
        "spotlight_feature_2",
        "spotlight_feature_3",
    };

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

    /// <summary>Cache keys with a background refresh in flight — the single-flight guard.</summary>
    private readonly ConcurrentDictionary<string, byte> _refreshing = new(StringComparer.Ordinal);

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
        var size = rowSize is >= 1 and <= 100 ? rowSize : settings.DefaultRowSize;

        string? cacheKey = personalized ? CacheKey(userId!.Value, size, capabilities) : null;
        if (cacheKey is not null && _cache.TryGet<CachedHome>(cacheKey, out var cached) && cached is not null)
        {
            _metrics.Increment("home.cache_hit");

            // Stale-while-revalidate: past the soft TTL the entry is still served instantly and a
            // single background rebuild refreshes it for the next viewer. Only a hard miss (evicted,
            // or past HardCacheTtl) ever makes someone wait through a full build.
            if (DateTime.UtcNow - cached.BuiltUtc > SoftCacheTtl)
            {
                RefreshInBackground(cacheKey, capabilities, size, userId);
            }

            return cached.Bundle;
        }

        var built = await BuildFreshAsync(capabilities, size, userId, personalized, settings, ct).ConfigureAwait(false);
        if (cacheKey is not null)
        {
            _cache.Set(cacheKey, new CachedHome(built, DateTime.UtcNow), HardCacheTtl);
        }

        _metrics.Increment("home.built");
        return built;
    }

    /// <summary>
    /// Rebuilds a stale cache entry off the request path so nobody waits for it. One rebuild per
    /// key however many viewers hit the stale entry at once, and a failure is invisible — the stale
    /// entry simply keeps being served until it expires.
    /// </summary>
    private void RefreshInBackground(string cacheKey, ClientCapabilities capabilities, int size, Guid? userId)
    {
        if (!_refreshing.TryAdd(cacheKey, 0))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                // Deliberately NOT the request's token: the refresh has to outlive the response that
                // triggered it. The per-row budgets bound it instead.
                var settings = await _settings.ResolveAsync(userId, CancellationToken.None).ConfigureAwait(false);
                var bundle = await BuildFreshAsync(capabilities, size, userId, personalized: true, settings, CancellationToken.None).ConfigureAwait(false);
                _cache.Set(cacheKey, new CachedHome(bundle, DateTime.UtcNow), HardCacheTtl);
                _metrics.Increment("home.refreshed");
            }
            catch (Exception)
            {
                _metrics.Increment("home.refresh.error");
            }
            finally
            {
                _refreshing.TryRemove(cacheKey, out _);
            }
        });
    }

    /// <summary>Builds the bundle from scratch, bypassing the cache entirely.</summary>
    private async Task<RenderBundle> BuildFreshAsync(
        ClientCapabilities capabilities,
        int size,
        Guid? userId,
        bool personalized,
        EngineSettings settings,
        CancellationToken ct)
    {
        var flags = settings.Features;

        // The affinity read is the one genuinely sequential step: the confidence it yields decides
        // the layout AND is handed to every row provider, so it has to resolve before the fan-out.
        // It's a stored-profile lookup, not an LLM or network call.
        var affinity = personalized && flags.Personalization
            ? await _personalization.GetAsync(userId!.Value, ct).ConfigureAwait(false)
            : null;
        var confidence = affinity?.Confidence ?? 0.0;

        var rowContext = new RowContext
        {
            UserId = personalized ? userId : null,
            Settings = settings,
            Confidence = confidence,
            RowSize = size,
        };

        // ── Fan-out ─────────────────────────────────────────────────────────────
        // Every row is produced by an INDEPENDENT task under its own time budget, so the page costs
        // the slowest single row instead of the sum of all of them, and one slow or broken
        // dependency degrades to "that row is missing" rather than a timed-out home. Producers
        // return data only — card selection and bundle assembly run single-threaded below, so
        // neither the bundle nor _cardSelector is ever touched concurrently.
        Task<PersonalResult?> personalTask = affinity is not null
            ? RowOrDefaultAsync<PersonalResult>(
                "foryou",
                ExpensiveRowBudget,
                c => BuildPersonalAsync(userId!.Value, size, settings, flags, capabilities, affinity, confidence, c),
                ct)
            : Task.FromResult<PersonalResult?>(null);

        Task<PendingRow?> bywTask = personalized && flags.Personalization && flags.SimilarityRows
            ? RowOrDefaultAsync<PendingRow>(
                "becauseyouwatched",
                ExpensiveRowBudget,
                c => BuildBecauseYouWatchedAsync(userId!.Value, size, flags, c),
                ct)
            : Task.FromResult<PendingRow?>(null);

        Task<PendingRow?> newSinceTask = personalized && flags.Personalization && flags.NewSinceAway
            ? RowOrDefaultAsync<PendingRow>("newsince", FastRowBudget, c => BuildNewSinceAsync(userId!.Value, size, c), ct)
            : Task.FromResult<PendingRow?>(null);

        Task<IReadOnlyList<ResumePoint>?> resumeTask = personalized && flags.ContinueWatching
            ? RowOrDefaultAsync<IReadOnlyList<ResumePoint>>(
                "continue",
                FastRowBudget,
                async c => await _resume.GetResumeAsync(userId!.Value, size, c).ConfigureAwait(false),
                ct)
            : Task.FromResult<IReadOnlyList<ResumePoint>?>(null);

        Task<LibraryRows?> libraryTask = RowOrDefaultAsync<LibraryRows>(
            "library",
            FastRowBudget,
            async c => await BuildLibraryRowsAsync(size, c).ConfigureAwait(false),
            ct);

        Task<PendingRow?> comingSoonTask = flags.ComingSoon
            ? RowOrDefaultAsync<PendingRow>("comingsoon", MediumRowBudget, c => BuildComingSoonAsync(userId, size, c), ct)
            : Task.FromResult<PendingRow?>(null);

        Task<IReadOnlyList<MoodRow>?> moodTask = flags.MoodCollections
            ? RowOrDefaultAsync<IReadOnlyList<MoodRow>>(
                "mood",
                MediumRowBudget,
                async c => await _mood.BuildAsync(size, c).ConfigureAwait(false),
                ct)
            : Task.FromResult<IReadOnlyList<MoodRow>?>(null);

        // Pluggable row providers: the justified discovery rows (trending with a pinch of taste,
        // You Might Like, pulled Because You Watched, the country row) — and any future row —
        // come from IRowProvider registrations, so new rows never require a HomeService change.
        // Each already opens its own DbContext, so they fan out like any other producer.
        var providerTasks = _rowProviders
            .Select(provider => RowOrDefaultAsync<IReadOnlyList<ProviderRow>>(
                $"provider.{provider.GetType().Name}",
                MediumRowBudget,
                async c => await provider.BuildAsync(rowContext, c).ConfigureAwait(false),
                ct))
            .ToList();

        // One wait for the whole page: the slowest row sets the pace, and none of these tasks can
        // fault (RowOrDefaultAsync absorbs everything except the caller giving up).
        var pending = new List<Task>
        {
            personalTask, bywTask, newSinceTask, resumeTask, libraryTask, comingSoonTask, moodTask,
        };
        pending.AddRange(providerTasks);
        await Task.WhenAll(pending).ConfigureAwait(false);

        // ── Assembly ────────────────────────────────────────────────────────────
        // Single-threaded, in the SAME order rows were emitted before the fan-out. That order is
        // the display order for anonymous bundles (which get no ReorderByConfidence pass), so it
        // must stay deterministic and independent of which task happened to finish first.
        var bundle = new RenderBundle { ContractVersion = 1 };

        var personal = await personalTask.ConfigureAwait(false);
        AddRow(bundle, personal?.Spotlight, capabilities);
        AddRow(bundle, personal?.ForYou, capabilities);
        AddRow(bundle, await bywTask.ConfigureAwait(false), capabilities);
        AddRow(bundle, await newSinceTask.ConfigureAwait(false), capabilities);

        var library = await libraryTask.ConfigureAwait(false);
        var recentlyAdded = library?.RecentlyAdded ?? Array.Empty<CatalogItem>();
        AddRow(bundle, "recent", "Recently Added", "recent", recentlyAdded, capabilities);

        // Continue Watching: the user's in-progress items, sourced from Jellyfin resume points.
        // Added after Recently Added, matching the home layout order.
        var resumePoints = await resumeTask.ConfigureAwait(false) ?? Array.Empty<ResumePoint>();
        AddResumeRow(bundle, resumePoints, capabilities);

        AddRow(bundle, await comingSoonTask.ConfigureAwait(false), capabilities);

        var providerPriorities = new Dictionary<string, (int Warm, int Cold)>(StringComparer.Ordinal);
        foreach (var task in providerTasks)
        {
            var providerRows = await task.ConfigureAwait(false) ?? Array.Empty<ProviderRow>();
            foreach (var providerRow in providerRows)
            {
                providerPriorities[providerRow.Id] = (providerRow.WarmPriority, providerRow.ColdPriority);
                AddRow(bundle, providerRow.Id, providerRow.Title, providerRow.Purpose, providerRow.Items, capabilities, providerRow.RowStyle, providerRow.Reasons);
            }
        }

        // Cinematic Spotlight showcases. Emitted here (not with the billboard) and given deep layout
        // priorities so they punctuate the feed rather than stacking a second near-full-screen
        // surface directly under the billboard. Candidates come from BELOW the billboard's slice;
        // with no personalized picks at all, the freshest arrivals stand in.
        var showcaseCandidates = personal?.ShowcaseCandidates is { Count: > 0 } fromForYou ? fromForYou : recentlyAdded;
        EmitSpotlightShowcases(bundle, showcaseCandidates, settings, flags, capabilities);

        // Mood-based collections (Milestone 8): themed rows (Mind Bending, Dark Thrillers, …).
        var moods = await moodTask.ConfigureAwait(false) ?? Array.Empty<MoodRow>();
        foreach (var mood in moods)
        {
            AddRow(bundle, $"mood:{mood.Id}", mood.Title, "mood", mood.Items, capabilities);
        }

        AddRow(bundle, "movies", "Movies", "library", library?.Movies ?? Array.Empty<CatalogItem>(), capabilities);
        AddRow(bundle, "series", "Series", "library", library?.Series ?? Array.Empty<CatalogItem>(), capabilities);

        // Confidence-driven layout: cold profiles lead with global/discovery rows, warm profiles
        // lead with personalized rows (spotlight + Continue Watching always stay pinned to the top).
        // LayoutPriority is a pure function of row id, so completion order can never affect this.
        if (personalized && flags.Personalization)
        {
            ReorderByConfidence(bundle, confidence, providerPriorities);
        }

        // Cross-row de-duplication in final display order: the highest-priority row keeps each title,
        // lower rows drop repeats — so the same movie/show stops appearing in nearly every row. Runs
        // for non-personalized bundles too (there insertion order already is the display order).
        DeduplicateRows(bundle, size);

        return bundle;
    }

    /// <summary>
    /// The personalized core: the recommender's candidate pool, LLM curation over it, and the two
    /// surfaces that share the result — the rotating Spotlight billboard and the "For You" row.
    /// Kept as ONE producer because all three read the same pool; splitting it would recommend twice.
    /// </summary>
    private async Task<PersonalResult?> BuildPersonalAsync(
        Guid userId,
        int size,
        EngineSettings settings,
        FeatureFlags flags,
        ClientCapabilities capabilities,
        AffinityVector affinity,
        double confidence,
        CancellationToken ct)
    {
        // Under LLM curation the local recommender only prefilters a wide pool and the LLM
        // genuinely selects the row; otherwise the local ranking IS the row (optionally
        // LLM-reordered by the legacy re-ranker). Both LLM paths are fail-soft passthroughs.
        var poolSize = flags.LlmCuration ? Math.Max(size * 3, 45) : size;
        var recs = await _recommender.RecommendAsync(userId, poolSize, ct).ConfigureAwait(false);
        var recItems = recs.Select(r => r.Item).ToList();
        if (recItems.Count == 0)
        {
            return null;
        }

        var reranked = await LlmOrPassThroughAsync(
            recItems,
            c => flags.LlmCuration
                ? _llmReRanker.CurateAsync(
                    new LlmCurationRequest
                    {
                        Purpose = "foryou",
                        Pool = recItems,
                        Count = size,
                        UserId = userId,
                        Confidence = confidence,
                        Context = "the viewer's main personalized 'For You' row",
                        WantTitle = true,
                    },
                    c)
                : _llmReRanker.ReRankAsync(userId, recItems, confidence, c),
            ct).ConfigureAwait(false);

        var forYouItems = reranked.Items.Take(size).ToList();
        var forYouTitle = string.IsNullOrWhiteSpace(reranked.RowTitle) ? "For You" : reranked.RowTitle!;

        // Every card gets a deterministic reason from the explanation engine; the LLM's richer
        // "why" (when configured + applied) overrides per item. So For You is self-explaining
        // with Groq off, and better with it on.
        var reasons = new Dictionary<long, string>();
        foreach (var item in forYouItems)
        {
            reasons[item.Id] = _explanation.ExplainRecommendation(item, affinity);
        }

        foreach (var (id, why) in reranked.Reasons)
        {
            reasons[id] = why;
        }

        // Spotlight billboard: the top For You picks (LLM-curated when active) as rotating Hero
        // cards, only when the feature is enabled and the client can render them.
        var spotlight = forYouItems.Count > 0 && flags.Spotlight && capabilities.SupportedCardTypes.Contains(CardType.Hero)
            ? new PendingRow("spotlight", "Spotlight", "spotlight", forYouItems.Take(settings.SpotlightCount).ToList(), RowStyle.Hero)
            : null;

        // Showcase candidates come from BELOW the billboard's slice, so the two surfaces never
        // feature the same title — seeing the identical item as both the top banner and a
        // full-screen showcase reads as a bug, not a recommendation.
        return new PersonalResult(
            spotlight,
            new PendingRow("foryou", forYouTitle, "recommended", forYouItems, Reasons: reasons),
            forYouItems.Skip(settings.SpotlightCount).ToList());
    }

    /// <summary>
    /// "Because You Watched X": seeded by the user's latest watch. The similarity engine prefilters
    /// a wide candidate pool; when the LLM is configured it then GENUINELY picks which of those a
    /// fan of the seed would watch next (with per-item "why" blurbs shown as card subtitles).
    /// Fail-soft: without an LLM the similarity order stands unchanged.
    /// </summary>
    private async Task<PendingRow?> BuildBecauseYouWatchedAsync(Guid userId, int size, FeatureFlags flags, CancellationToken ct)
    {
        var byw = await _similarity.BecauseYouWatchedAsync(userId, size * 2, ct).ConfigureAwait(false);
        if (byw.Seed is not { } seed || byw.Items.Count == 0)
        {
            return null;
        }

        var candidates = byw.Items.Select(r => r.Item).ToList();
        var picked = await LlmOrPassThroughAsync(
            candidates,
            c => flags.LlmCuration
                ? _llmReRanker.CurateAsync(
                    new LlmCurationRequest
                    {
                        Purpose = "byw",
                        Pool = candidates,
                        Count = size,
                        Seed = seed,
                        Context = "titles a fan of the just-watched seed would genuinely watch next",
                        CacheTtl = TimeSpan.FromHours(24),
                    },
                    c)
                : _llmReRanker.PickForSeedAsync(seed, candidates, c),
            ct).ConfigureAwait(false);

        var title = string.IsNullOrWhiteSpace(seed.Title) ? "Because You Watched" : $"Because You Watched {seed.Title}";
        return new PendingRow(
            "becauseyouwatched",
            title,
            "similar",
            picked.Items.Take(size).ToList(),
            Reasons: picked.Applied ? picked.Reasons : null);
    }

    /// <summary>
    /// "New Since You Were Away": strict release/availability events (aired/downloaded/newly
    /// available) since the user's last visit — backlog is handled by "Continue the Story".
    /// </summary>
    private async Task<PendingRow?> BuildNewSinceAsync(Guid userId, int size, CancellationToken ct)
    {
        var newSince = await _newSince.GetAsync(userId, size, ct).ConfigureAwait(false);
        return newSince.Items.Count == 0
            ? null
            : new PendingRow(
                "newsince",
                "New Since You Were Away",
                "newsince",
                newSince.Items,
                Reasons: newSince.Reasons.Count > 0 ? newSince.Reasons : null);
    }

    /// <summary>
    /// Coming Soon (For You): upcoming episodes/releases for catalog titles (*arr calendar → TMDB
    /// fallback), filtered by relevance — monitored series, new seasons of shows you watch, and
    /// taste-aligned high-quality upcoming. Per-item labels carry the sub-type.
    /// </summary>
    private async Task<PendingRow?> BuildComingSoonAsync(Guid? userId, int size, CancellationToken ct)
    {
        var upcoming = await _upcoming.GetUpcomingAsync(userId, size, ct).ConfigureAwait(false);
        return upcoming.Count == 0
            ? null
            : new PendingRow(
                "comingsoon",
                "Coming Soon",
                "upcoming",
                upcoming.Select(u => u.Item).ToList(),
                Reasons: upcoming.ToDictionary(u => u.Item.Id, u => u.Label));
    }

    /// <summary>
    /// The three plain catalog rows (Recently Added / Movies / Series). One context for all three:
    /// they're indexed reads over the same table, so splitting them across connections buys nothing.
    /// </summary>
    private async Task<LibraryRows> BuildLibraryRowsAsync(int size, CancellationToken ct)
    {
        await using var db = _factory.Create();

        var recentlyAdded = await db.CatalogItems
            .OrderByDescending(c => c.DateAdded)
            .Take(size * OverFetchFactor)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var movies = await db.CatalogItems
            .Where(c => c.MediaType == MediaType.Movie)
            .OrderByDescending(c => c.DateAdded)
            .Take(size * OverFetchFactor)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var series = await db.CatalogItems
            .Where(c => c.MediaType == MediaType.Series)
            .OrderByDescending(c => c.DateAdded)
            .Take(size * OverFetchFactor)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return new LibraryRows(recentlyAdded, movies, series);
    }

    /// <summary>Runs one row producer under its own time budget — see <see cref="RowBudget"/>.</summary>
    private Task<T?> RowOrDefaultAsync<T>(
        string name,
        TimeSpan budget,
        Func<CancellationToken, Task<T?>> build,
        CancellationToken ct)
        where T : class
        => RowBudget.OrDefaultAsync(_metrics, name, budget, build, ct);

    /// <summary>
    /// Runs an LLM curation step under <see cref="LlmStepBudget"/>, degrading to the local ordering
    /// when it's too slow — curation is polish on a row we can already ship.
    /// </summary>
    private Task<LlmReRankResult> LlmOrPassThroughAsync(
        IReadOnlyList<CatalogItem> localOrder,
        Func<CancellationToken, Task<LlmReRankResult>> curate,
        CancellationToken ct)
        => RowBudget.OrFallbackAsync(_metrics, "llm", LlmStepBudget, curate, LlmReRankResult.PassThrough(localOrder), ct);

    /// <summary>
    /// A resolved row in the shape <see cref="AddRow"/> renders. Producers return these rather than
    /// touching the bundle, so card selection and assembly stay single-threaded below the fan-out.
    /// </summary>
    private sealed record PendingRow(
        string Id,
        string Title,
        string Purpose,
        IReadOnlyList<CatalogItem> Items,
        RowStyle RowStyle = RowStyle.Standard,
        IReadOnlyDictionary<long, string>? Reasons = null);

    /// <summary>
    /// What the personalized producer yields: the Spotlight billboard and "For You" rows, plus the
    /// candidates the cinematic showcases draw from.
    /// </summary>
    private sealed record PersonalResult(
        PendingRow? Spotlight,
        PendingRow ForYou,
        IReadOnlyList<CatalogItem> ShowcaseCandidates);

    /// <summary>A cached home plus when it was built, so staleness can be judged independently
    /// of the entry's own (hard) expiry.</summary>
    private sealed record CachedHome(RenderBundle Bundle, DateTime BuiltUtc);

    /// <summary>The plain catalog rows, produced together off one database context.</summary>
    private sealed record LibraryRows(
        IReadOnlyList<CatalogItem> RecentlyAdded,
        IReadOnlyList<CatalogItem> Movies,
        IReadOnlyList<CatalogItem> Series);

    /// <summary>
    /// Emits the cinematic Spotlight showcase rows: each holds exactly ONE item and is rendered
    /// nearly full-screen by the client, playing its trailer inline on focus.
    /// </summary>
    /// <remarks>
    /// Distinct from the Hero billboard, which is a single rotating surface pinned to the top. These
    /// are several single-item rows spread through the feed, and each gets a unique id because the
    /// client renders rows keyed by id. A title is only worth a full screen if it has backdrop art
    /// to fill it, so candidates without one are skipped rather than showing a bare gradient.
    /// </remarks>
    private void EmitSpotlightShowcases(
        RenderBundle bundle,
        IReadOnlyList<CatalogItem> candidates,
        EngineSettings settings,
        FeatureFlags flags,
        ClientCapabilities capabilities)
    {
        var wanted = Math.Min(settings.SpotlightShowcaseCount, SpotlightShowcaseIds.Length);
        if (wanted <= 0
            || !flags.SpotlightShowcase
            || !capabilities.SupportedCardTypes.Contains(CardType.Spotlight))
        {
            return;
        }

        var used = new HashSet<string>(StringComparer.Ordinal);
        var slot = 0;
        foreach (var item in candidates)
        {
            if (slot >= wanted)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(item.BackdropImageUrl))
            {
                continue;
            }

            // Never showcase the same title twice across the showcases themselves.
            if (!used.Add($"{item.Id}"))
            {
                continue;
            }

            AddRow(
                bundle,
                SpotlightShowcaseIds[slot],
                "In the Spotlight",
                "spotlight_showcase",
                new[] { item },
                capabilities,
                RowStyle.Spotlight);
            slot++;
        }
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

            // Showcases are their own presentation surface, like the billboard. They hold a single
            // item, so de-duplicating one against an earlier row would empty it and the row would
            // then be dropped entirely — the showcase would silently vanish whenever its title also
            // appeared in For You. They also deliberately do NOT reserve their title, so the normal
            // rows below still carry it.
            if (row.RowStyle == RowStyle.Spotlight)
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

            // Cinematic showcases sit DEEP and spread apart, never near the top: the billboard
            // already owns the screen up there, and a second near-full-screen surface beneath it
            // would make the viewer scroll past two giant panels before reaching anything
            // browsable. Interleaved with the library/discovery rows rather than adjacent.
            case "spotlight_feature_1": return 7;
            case "spotlight_feature_2": return 10;
            case "spotlight_feature_3": return 13;
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

    /// <summary>Renders a producer's row, or does nothing when that producer yielded none.</summary>
    private void AddRow(RenderBundle bundle, PendingRow? row, ClientCapabilities capabilities)
    {
        if (row is not null)
        {
            AddRow(bundle, row.Id, row.Title, row.Purpose, row.Items, capabilities, row.RowStyle, row.Reasons);
        }
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
