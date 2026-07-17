using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wholphin.Engine.Data;
using Wholphin.Engine.Data.Entities;
using Wholphin.Engine.Data.Enums;
using Wholphin.Engine.Diagnostics;
using Wholphin.Engine.Integrations.Tmdb;
using Wholphin.Engine.Personalization;
using Wholphin.Engine.Settings;

namespace Wholphin.Engine.Discovery;

/// <summary>
/// The discovery pipeline orchestrator. Loads the run context once (the stages themselves never
/// touch the database), then runs the strict stage chain — sources → aggregation → eligibility →
/// scoring → diversity → selection → persistence — and reports the funnel. Replaces the old
/// indiscriminate <c>DiscoveryImporter</c>: nothing is imported without a per-user (or honest
/// global) justification pick.
/// </summary>
public class DiscoveryPipeline : IDiscoveryOrchestrator
{
    /// <summary>How old a taste profile may get before a pull rebuilds it first.</summary>
    public static readonly TimeSpan ProfileMaxAge = TimeSpan.FromHours(24);

    /// <summary>Memory rows untouched this long are pruned by the sweep.</summary>
    public static readonly TimeSpan MemoryRetention = TimeSpan.FromDays(180);

    /// <summary>Funnel-run rows older than this are pruned by the sweep.</summary>
    public static readonly TimeSpan RunRetention = TimeSpan.FromDays(30);

    private readonly IReadOnlyList<IDiscoverySource> _sources;
    private readonly EligibilityFilter _filter;
    private readonly CandidateScorer _scorer;
    private readonly DiversityStage _diversity;
    private readonly SelectionStage _selection;
    private readonly PickPersistence _persistence;
    private readonly ITmdbClient _tmdb;
    private readonly ITasteProfileService _tasteProfiles;
    private readonly IPersonalizationService _personalization;
    private readonly ISettingsService _settings;
    private readonly IWholphinDbContextFactory _factory;
    private readonly IEngineMetrics _metrics;
    private readonly ILogger<DiscoveryPipeline> _logger;

    /// <summary>Initializes a new instance of the <see cref="DiscoveryPipeline"/> class.</summary>
    /// <param name="sources">The registered discovery sources (plug-ins).</param>
    /// <param name="filter">The eligibility filter stage.</param>
    /// <param name="scorer">The scoring stage.</param>
    /// <param name="diversity">The diversity reshuffle stage.</param>
    /// <param name="selection">The selection stage.</param>
    /// <param name="persistence">The persistence stage.</param>
    /// <param name="tmdb">The TMDB client (gating + genre maps).</param>
    /// <param name="tasteProfiles">The taste-profile file service.</param>
    /// <param name="personalization">The personalization service.</param>
    /// <param name="settings">The layered settings resolver.</param>
    /// <param name="factory">Database context factory (context loads + sweep).</param>
    /// <param name="metrics">Operational metrics.</param>
    /// <param name="logger">The logger.</param>
    public DiscoveryPipeline(
        IEnumerable<IDiscoverySource> sources,
        EligibilityFilter filter,
        CandidateScorer scorer,
        DiversityStage diversity,
        SelectionStage selection,
        PickPersistence persistence,
        ITmdbClient tmdb,
        ITasteProfileService tasteProfiles,
        IPersonalizationService personalization,
        ISettingsService settings,
        IWholphinDbContextFactory factory,
        IEngineMetrics metrics,
        ILogger<DiscoveryPipeline> logger)
    {
        _sources = sources.ToList();
        _filter = filter;
        _scorer = scorer;
        _diversity = diversity;
        _selection = selection;
        _persistence = persistence;
        _tmdb = tmdb;
        _tasteProfiles = tasteProfiles;
        _personalization = personalization;
        _settings = settings;
        _factory = factory;
        _metrics = metrics;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<TastePullResult> PullForUserAsync(Guid userId, CancellationToken ct = default)
    {
        try
        {
            var settings = await _settings.ResolveAsync(userId, ct).ConfigureAwait(false);
            if (!settings.Features.TasteDiscovery)
            {
                _metrics.Increment("discovery.pull.skip");
                return new TastePullResult(true, "disabled", 0, 0);
            }

            if (!_tmdb.IsConfigured)
            {
                _metrics.Increment("discovery.pull.skip");
                return new TastePullResult(true, "tmdb", 0, 0);
            }

            var tuning = DiscoveryTuning.Resolve();
            var profile = await _tasteProfiles.GetAsync(userId, ct).ConfigureAwait(false);
            if (profile is null || DateTime.UtcNow - profile.GeneratedAt > ProfileMaxAge)
            {
                profile = await _tasteProfiles.RebuildAsync(userId, ct).ConfigureAwait(false);
            }

            // The cold-start gate: no taste, no pull, no blind fallback. Cold users get the
            // justified global rows (library + trending + country) until taste accrues.
            if (profile.Confidence < tuning.MinConfidence)
            {
                _metrics.Increment("discovery.pull.skip");
                return new TastePullResult(true, "confidence", 0, 0);
            }

            var context = await BuildContextAsync(userId, profile, settings, countryCode: null, tuning, ct).ConfigureAwait(false);

            // LLM-primary orchestration: when AI discovery is on, the LLM source generates the
            // pool and the TMDB sources only fill if it under-delivers; otherwise (feature off, or
            // no LLM source registered) every per-user source runs exactly as before.
            var perUser = _sources.Where(s => s.Scope == DiscoverySourceScope.PerUser).ToList();
            var llmActive = settings.Features.LlmDiscovery && perUser.Any(s => s.Kind == DiscoveryPickKind.LlmPick);
            var primary = llmActive ? perUser.Where(s => s.Kind == DiscoveryPickKind.LlmPick).ToList() : perUser;
            var fill = llmActive ? perUser.Where(s => s.Kind != DiscoveryPickKind.LlmPick).ToList() : new List<IDiscoverySource>();
            var result = await RunAsync(context, primary, fill, ct).ConfigureAwait(false);

            _metrics.Increment("discovery.pull.ok");
            _metrics.Increment("discovery.imported", result.Imported);
            return new TastePullResult(false, null, result.Imported, result.PicksWritten);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _metrics.Increment("discovery.pull.error");
            _logger.LogWarning(ex, "Orca Engine: taste pull failed for {UserId}.", userId);
            return new TastePullResult(true, "error", 0, 0);
        }
    }

    /// <inheritdoc />
    public async Task<GlobalPullResult> PullGlobalAsync(CancellationToken ct = default)
    {
        var countryPicks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var settings = await _settings.ResolveAsync(null, ct).ConfigureAwait(false);
            if (!settings.Features.TasteDiscovery || !_tmdb.IsConfigured)
            {
                return new GlobalPullResult(0, countryPicks);
            }

            var tuning = DiscoveryTuning.Resolve();

            // Trending: one world-wide list shared by everyone.
            var trendingContext = await BuildContextAsync(Guid.Empty, null, settings, countryCode: null, tuning, ct).ConfigureAwait(false);
            var trendingSources = _sources.Where(s => s.Scope == DiscoverySourceScope.Global && s.Kind == DiscoveryPickKind.Trending).ToList();
            var trending = await RunAsync(trendingContext, trendingSources, Array.Empty<IDiscoverySource>(), ct).ConfigureAwait(false);

            // Country lists: one pull per distinct configured country.
            var countrySources = _sources.Where(s => s.Scope == DiscoverySourceScope.Global && s.Kind == DiscoveryPickKind.Country).ToList();
            foreach (var cc in await ActiveCountriesAsync(settings, ct).ConfigureAwait(false))
            {
                var countryContext = await BuildContextAsync(Guid.Empty, null, settings, cc, tuning, ct).ConfigureAwait(false);
                var result = await RunAsync(countryContext, countrySources, Array.Empty<IDiscoverySource>(), ct).ConfigureAwait(false);
                countryPicks[cc] = result.PicksWritten;
            }

            return new GlobalPullResult(trending.PicksWritten, countryPicks);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _metrics.Increment("discovery.pull.error");
            _logger.LogWarning(ex, "Orca Engine: global discovery pull failed.");
            return new GlobalPullResult(0, countryPicks);
        }
    }

    /// <inheritdoc />
    public async Task<SweepResult> SweepAsync(CancellationToken ct = default)
    {
        try
        {
            var now = DateTime.UtcNow;
            var tuning = DiscoveryTuning.Resolve();
            await using var db = _factory.Create();

            // 1) Ignored-cycle decay: live per-user picks that have been on screen a full TTL with
            //    zero engagement decay the item's interest and leave the rows (deleting the pick
            //    resets the cycle anchor; the item may re-qualify later against its reduced
            //    interest, or sit out its cooldown).
            var decayed = 0;
            var staleBefore = now - TimeSpan.FromDays(tuning.PickTtlDays);
            var stale = await db.UserDiscoveryPicks
                .Where(p => p.UserId != Guid.Empty && p.CreatedAt < staleBefore)
                .ToListAsync(ct)
                .ConfigureAwait(false);
            if (stale.Count > 0)
            {
                var itemIds = stale.Select(p => p.CatalogItemId).Distinct().ToList();
                var items = await db.CatalogItems.AsNoTracking()
                    .Where(c => itemIds.Contains(c.Id) && c.TmdbId != null)
                    .ToDictionaryAsync(c => c.Id, ct)
                    .ConfigureAwait(false);
                var userIds = stale.Select(p => p.UserId).Distinct().ToList();
                var memories = await db.UserItemMemories
                    .Where(m => userIds.Contains(m.UserId))
                    .ToListAsync(ct)
                    .ConfigureAwait(false);
                var memoryByKey = memories.ToDictionary(m => (m.UserId, m.TmdbId, m.MediaType), m => m);

                foreach (var pick in stale)
                {
                    if (!items.TryGetValue(pick.CatalogItemId, out var item))
                    {
                        db.UserDiscoveryPicks.Remove(pick);
                        continue;
                    }

                    memoryByKey.TryGetValue((pick.UserId, item.TmdbId!.Value, item.MediaType), out var memory);
                    var engaged = memory?.LastEngagedAt is { } at && at >= pick.CreatedAt;
                    if (!engaged && memory is not null)
                    {
                        InterestModel.OnIgnoredCycle(memory, now);
                        decayed++;
                    }

                    db.UserDiscoveryPicks.Remove(pick);
                }

                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }

            // 2) Expired picks (safety net for anything the decay pass didn't already remove).
            var expired = await db.UserDiscoveryPicks
                .Where(p => p.ExpiresAt != null && p.ExpiresAt < now)
                .ExecuteDeleteAsync(ct)
                .ConfigureAwait(false);

            // 3) The no-firehose guarantee: external Request-state rows nothing references any
            //    more (no request, no behavior, no live pick) leave the catalog.
            var orphans = await db.CatalogItems
                .Where(c => c.JellyfinItemId == null && c.Availability == AvailabilityState.Request)
                .Where(c => c.TmdbId == null || !db.MediaRequests.Any(r => r.TmdbId == c.TmdbId.Value))
                .Where(c => !db.BehaviorEvents.Any(e => e.CatalogItemId == c.Id))
                .Where(c => !db.UserDiscoveryPicks.Any(p => p.CatalogItemId == c.Id))
                .ExecuteDeleteAsync(ct)
                .ConfigureAwait(false);

            // 4) Retention: memory untouched for half a year, funnel reports older than a month.
            await db.UserItemMemories
                .Where(m => m.UpdatedAt < now - MemoryRetention)
                .ExecuteDeleteAsync(ct)
                .ConfigureAwait(false);
            await db.DiscoveryRuns
                .Where(r => r.StartedAt < now - RunRetention)
                .ExecuteDeleteAsync(ct)
                .ConfigureAwait(false);

            _metrics.Increment("discovery.swept.picks", stale.Count + expired);
            _metrics.Increment("discovery.swept.rows", orphans);
            if (decayed > 0)
            {
                _metrics.Increment("discovery.decayed", decayed);
            }

            if (stale.Count + expired + orphans > 0)
            {
                _logger.LogInformation(
                    "Orca Engine: discovery sweep removed {Picks} picks, decayed {Decayed} items, deleted {Rows} orphan rows.",
                    stale.Count + expired,
                    decayed,
                    orphans);
            }

            return new SweepResult(stale.Count + expired, decayed, orphans);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Orca Engine: discovery sweep failed.");
            return new SweepResult(0, 0, 0);
        }
    }

    /// <summary>Picks the primary phase must yield (post-eligibility) before the fill phase is skipped — roughly one row.</summary>
    public const int LlmFillThreshold = 12;

    /// <summary>
    /// Runs the strict stage chain over one context. Sources run in two phases: <paramref name="primary"/>
    /// always gathers; <paramref name="fill"/> (empty on global runs) gathers only when the primary batch
    /// under-fills after eligibility, so a healthy LLM run isn't diluted while a failed one degrades to
    /// exactly the old TMDB behavior. The chain itself runs once over the union.
    /// </summary>
    private async Task<PickPersistence.PersistenceResult> RunAsync(
        DiscoveryContext context,
        IReadOnlyList<IDiscoverySource> primary,
        IReadOnlyList<IDiscoverySource> fill,
        CancellationToken ct)
    {
        var report = new DiscoveryRunReport { UserId = context.UserId, StartedAt = context.Now };

        // Stage 1: candidate generation (each source tags its own attributions).
        var generated = new List<DiscoveryCandidate>();
        foreach (var source in primary)
        {
            var batch = await source.GatherAsync(context, ct).ConfigureAwait(false);
            generated.AddRange(batch);
            report.GeneratedBySource[source.Name] = report.GeneratedBySource.GetValueOrDefault(source.Name) + batch.Count;
            _metrics.Increment($"discovery.source.{source.Name}.generated", batch.Count);
        }

        if (fill.Count > 0)
        {
            // Pre-count with the pure eligibility filter: an LLM batch full of in-library or
            // blacklisted titles counts as under-filled and triggers the fill phase. Dedupe
            // non-mutatingly here — the real Aggregate below merges attributions in place and
            // must see them un-merged.
            var deduped = generated
                .GroupBy(c => (c.Result.TmdbId, c.Result.MediaType))
                .Select(g => g.First())
                .ToList();
            var eligible = _filter.Apply(context, deduped).Kept.Count;
            if (eligible < LlmFillThreshold)
            {
                _metrics.Increment("discovery.llm.fill");
                foreach (var source in fill)
                {
                    var batch = await source.GatherAsync(context, ct).ConfigureAwait(false);
                    generated.AddRange(batch);
                    report.GeneratedBySource[source.Name] = report.GeneratedBySource.GetValueOrDefault(source.Name) + batch.Count;
                    _metrics.Increment($"discovery.source.{source.Name}.generated", batch.Count);
                }
            }
        }

        report.Generated = generated.Count;

        // Aggregation (pure normalization, not a stage): merge duplicates by (TMDB id, type),
        // unioning attributions; a seeded proposal wins the primary kind so the seed reason survives.
        var aggregated = Aggregate(generated);

        // Stage 2: hard eligibility rules.
        var eligibility = _filter.Apply(context, aggregated);
        report.FilteredOut = eligibility.Dropped.Values.Sum();
        report.FilterReasons = new Dictionary<FilterReason, int>(eligibility.Dropped);

        // Stage 3: pure ranking signals.
        var scoring = await _scorer.ScoreAsync(context, eligibility.Kept, ct).ConfigureAwait(false);
        report.Scored = scoring.Scored.Count;

        // Stage 4: diversity reshuffle.
        var diversity = _diversity.Apply(scoring.Scored);
        report.DiversityReordered = diversity.Reordered;

        // Stage 5: final cuts.
        var selection = _selection.Select(context, diversity.Ranked, scoring.TasteFloor);
        report.BelowThreshold = selection.BelowThreshold;

        // Stage 6: the only writes.
        return await _persistence.PersistAsync(context, selection.Picks, report, ct).ConfigureAwait(false);
    }

    private static List<DiscoveryCandidate> Aggregate(List<DiscoveryCandidate> generated)
    {
        var byKey = new Dictionary<(int, MediaType), DiscoveryCandidate>();
        foreach (var candidate in generated)
        {
            var key = (candidate.Result.TmdbId, candidate.Result.MediaType);
            if (!byKey.TryGetValue(key, out var existing))
            {
                byKey[key] = candidate;
                continue;
            }

            existing.Attributions.AddRange(candidate.Attributions);
            if (existing.Seed is null && candidate.Seed is not null)
            {
                existing.Seed = candidate.Seed;
                existing.Kind = candidate.Kind;
            }

            existing.LlmRationale ??= candidate.LlmRationale;
        }

        return byKey.Values.ToList();
    }

    private async Task<DiscoveryContext> BuildContextAsync(
        Guid userId,
        UserTasteProfile? profile,
        EngineSettings settings,
        string? countryCode,
        DiscoveryTuning tuning,
        CancellationToken ct)
    {
        var context = new DiscoveryContext
        {
            UserId = userId,
            Profile = profile,
            Settings = settings,
            CountryCode = countryCode,
            Tuning = tuning,
            Now = DateTime.UtcNow,
        };

        await using var db = _factory.Create();
        context.LibraryTmdbIds = (await db.CatalogItems.AsNoTracking()
                .Where(c => c.JellyfinItemId != null && c.TmdbId != null)
                .Select(c => c.TmdbId!.Value)
                .ToListAsync(ct)
                .ConfigureAwait(false))
            .ToHashSet();
        context.BlacklistedTmdbIds = (await db.CatalogItems.AsNoTracking()
                .Where(c => c.Availability == AvailabilityState.Unavailable && c.TmdbId != null)
                .Select(c => c.TmdbId!.Value)
                .ToListAsync(ct)
                .ConfigureAwait(false))
            .ToHashSet();

        if (userId != Guid.Empty)
        {
            context.Affinity = await _personalization.GetAsync(userId, ct).ConfigureAwait(false);
            context.Memory = (await db.UserItemMemories.AsNoTracking()
                    .Where(m => m.UserId == userId)
                    .ToListAsync(ct)
                    .ConfigureAwait(false))
                .ToDictionary(m => (m.TmdbId, m.MediaType), m => m);

            if (profile is { Seeds.Count: > 0 })
            {
                var seedIds = profile.Seeds.Select(s => s.CatalogItemId).ToList();
                context.SeedItems = await db.CatalogItems.AsNoTracking()
                    .Where(c => seedIds.Contains(c.Id))
                    .ToListAsync(ct)
                    .ConfigureAwait(false);
            }

            // Explicit dislikes feed the LLM source's negative constraints; skip the query when
            // the feature is off so plain runs stay exactly as cheap as before.
            if (settings.Features.LlmDiscovery)
            {
                context.DislikedTitles = await LoadDislikedTitlesAsync(db, userId, ct).ConfigureAwait(false);
            }

            // Genre name → id inversions for the filtered-discover source.
            var genreIds = new Dictionary<MediaType, IReadOnlyDictionary<string, int>>();
            foreach (var mediaType in new[] { MediaType.Movie, MediaType.Series })
            {
                var map = await _tmdb.GetGenreMapAsync(mediaType, ct).ConfigureAwait(false);
                var inverted = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var (id, name) in map)
                {
                    inverted[name] = id;
                }

                genreIds[mediaType] = inverted;
            }

            context.GenreIdsByName = genreIds;
        }

        return context;
    }

    /// <summary>
    /// The user's explicit rejections (thumbs down, or a rating of 3 or less), joined to catalog
    /// titles, newest first, capped at 10 — the LLM prompt's "do not recommend anything similar"
    /// constraints.
    /// </summary>
    private static async Task<IReadOnlyList<Llm.HistoryLine>> LoadDislikedTitlesAsync(
        WholphinDbContext db,
        Guid userId,
        CancellationToken ct)
    {
        var disliked = await db.BehaviorEvents.AsNoTracking()
            .Where(e => e.UserId == userId && e.CatalogItemId != null)
            .Where(e => e.EventType == BehaviorEventType.ThumbsDown
                || (e.EventType == BehaviorEventType.Rated && e.Value <= 3))
            .OrderByDescending(e => e.Id)
            .Select(e => e.CatalogItemId!.Value)
            .Take(50)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (disliked.Count == 0)
        {
            return Array.Empty<Llm.HistoryLine>();
        }

        var itemIds = disliked.Distinct().Take(10).ToList();
        var items = await db.CatalogItems.AsNoTracking()
            .Where(c => itemIds.Contains(c.Id))
            .Select(c => new { c.Id, c.Title, c.ProductionYear })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var byId = items.ToDictionary(i => i.Id);

        return itemIds
            .Where(byId.ContainsKey)
            .Select(id => new Llm.HistoryLine(byId[id].Title, byId[id].ProductionYear))
            .Where(line => !string.IsNullOrWhiteSpace(line.Title))
            .ToList();
    }

    /// <summary>The distinct countries to pull for: every user's <c>pref.country</c> plus the admin region.</summary>
    private async Task<IReadOnlyList<string>> ActiveCountriesAsync(EngineSettings settings, CancellationToken ct)
    {
        var countries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (IsCountry(settings.CountryCode))
        {
            countries.Add(settings.CountryCode.ToUpperInvariant());
        }

        await using var db = _factory.Create();
        var raw = await db.UserSettings.AsNoTracking()
            .Where(s => s.Key == SettingsService.KeyCountry)
            .Select(s => s.ValueJson)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        foreach (var value in raw)
        {
            var cc = value.Trim().Trim('"').ToUpperInvariant();
            if (IsCountry(cc))
            {
                countries.Add(cc);
            }
        }

        return countries.ToList();
    }

    private static bool IsCountry(string? value)
        => value is { Length: 2 } && value.All(char.IsAsciiLetterUpper);
}
