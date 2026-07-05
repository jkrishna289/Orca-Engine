using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wholphin.Engine.Caching;
using Wholphin.Engine.Data;
using Wholphin.Engine.Data.Entities;
using Wholphin.Engine.Data.Enums;
using Wholphin.Engine.Integrations.Arr;
using Wholphin.Engine.Integrations.Tmdb;
using Wholphin.Engine.Intelligence;

namespace Wholphin.Engine.Catalog;

/// <summary>
/// Default <see cref="IUpcomingProvider"/>. Pulls the *arr calendar for the next few weeks and maps
/// entries to catalog items by TMDB id (TMDB <c>next_episode_to_air</c> fills the gaps), then applies
/// a relevance layer — monitored series lead, new seasons of invested-in shows follow, everything
/// else must clear an intent + quality bar (<see cref="ComingSoonClassifier"/>). Cached 12h per user.
/// </summary>
public class UpcomingProvider : IUpcomingProvider
{
    private const int UpcomingDays = 21;
    private const int MaxResults = 40;
    private const int MaxTmdbFallback = 25;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(12);

    private readonly IArrClient _arr;
    private readonly ITmdbClient _tmdb;
    private readonly IWholphinDbContextFactory _factory;
    private readonly ISeriesIntelligenceEngine _engine;
    private readonly ICache _cache;
    private readonly ILogger<UpcomingProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="UpcomingProvider"/> class.
    /// </summary>
    public UpcomingProvider(
        IArrClient arr,
        ITmdbClient tmdb,
        IWholphinDbContextFactory factory,
        ISeriesIntelligenceEngine engine,
        ICache cache,
        ILogger<UpcomingProvider> logger)
    {
        _arr = arr;
        _tmdb = tmdb;
        _factory = factory;
        _engine = engine;
        _cache = cache;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<UpcomingItem>> GetUpcomingAsync(Guid? userId, int limit, CancellationToken ct = default)
    {
        if (limit <= 0)
        {
            return Array.Empty<UpcomingItem>();
        }

        var cacheKey = CacheKeyFor(userId);
        if (_cache.TryGet<List<UpcomingItem>>(cacheKey, out var cached) && cached is not null)
        {
            return cached.Take(limit).ToList();
        }

        var built = await BuildAsync(userId, ct).ConfigureAwait(false);
        _cache.Set(cacheKey, built, CacheTtl);
        return built.Take(limit).ToList();
    }

    private static string CacheKeyFor(Guid? userId)
        => $"upcoming:{(userId is { } u && u != Guid.Empty ? u.ToString("N") : "anon")}";

    private async Task<List<UpcomingItem>> BuildAsync(Guid? userId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var end = now.AddDays(UpcomingDays);

        await using var db = _factory.Create();
        var byTmdb = (await db.CatalogItems
                .Where(c => c.TmdbId != null)
                .ToListAsync(ct)
                .ConfigureAwait(false))
            .GroupBy(c => c.TmdbId!.Value)
            .ToDictionary(g => g.Key, g => g.First());

        var candidates = new Dictionary<long, Candidate>();

        // Primary: the *arr calendar (most accurate for the user's actual schedule).
        if (_arr.IsConfigured)
        {
            try
            {
                var entries = await _arr.GetCalendarAsync(now, end, ct).ConfigureAwait(false);
                foreach (var e in entries.OrderBy(e => e.AirDateUtc))
                {
                    if (e.TmdbId is not { } tmdb || !byTmdb.TryGetValue(tmdb, out var item))
                    {
                        continue;
                    }

                    // Keep the earliest upcoming entry per catalog item.
                    if (!candidates.ContainsKey(item.Id))
                    {
                        candidates[item.Id] = new Candidate(item, e.AirDateUtc, e.MediaType, e.SeasonNumber, e.EpisodeNumber);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Orca Engine: *arr calendar fetch failed for Coming Soon.");
            }
        }

        // Fallback: TMDB next_episode_to_air for catalog series not covered above (bounded).
        if (_tmdb.IsConfigured)
        {
            var series = byTmdb.Values
                .Where(c => c.MediaType == MediaType.Series && !candidates.ContainsKey(c.Id))
                .Take(MaxTmdbFallback)
                .ToList();
            foreach (var item in series)
            {
                ct.ThrowIfCancellationRequested();
                var next = await _tmdb.GetNextEpisodeAsync(item.TmdbId!.Value, ct).ConfigureAwait(false);
                if (next is null || next.AirDate < now || next.AirDate > end)
                {
                    continue;
                }

                candidates[item.Id] = new Candidate(item, next.AirDate, MediaType.Series, next.SeasonNumber, next.EpisodeNumber);
            }
        }

        if (candidates.Count == 0)
        {
            return new List<UpcomingItem>();
        }

        // Relevance layer: monitored state (server-wide) + this user's affinity + per-series watch state.
        var monitor = _arr.IsConfigured
            ? await _arr.GetMonitorStateAsync(ct).ConfigureAwait(false)
            : new ArrMonitorState();
        var scorer = await _engine.GetIntentScorerAsync(userId, ct).ConfigureAwait(false);

        var result = new List<UpcomingItem>();
        foreach (var c in candidates.Values.OrderBy(c => c.AirUtc))
        {
            var isSeries = c.Type == MediaType.Series;
            var isMonitored = isSeries
                ? monitor.IsSeriesMonitored(c.Item.TmdbId, c.Season)
                : monitor.IsMovieMonitored(c.Item.TmdbId);
            var watchedPrior = isSeries && userId is { } u && u != Guid.Empty && UserWatchedPrior(u, c.Item);
            var followsFranchise = scorer.FollowsFranchise(c.Item);
            var intent = scorer.Score(c.Item);

            var kind = ComingSoonClassifier.Classify(
                isSeries, isMonitored, watchedPrior, followsFranchise, intent, scorer.Confidence, c.Item.CommunityRating);
            if (kind == ComingSoonKind.Excluded)
            {
                continue;
            }

            result.Add(new UpcomingItem(c.Item, c.AirUtc, LabelFor(kind, c.Type, c.Season, c.Episode, c.AirUtc)));
        }

        return result.Take(MaxResults).ToList();
    }

    private bool UserWatchedPrior(Guid userId, CatalogItem item)
    {
        if (item.JellyfinItemId is not { } jf || jf == Guid.Empty)
        {
            return false;
        }

        var state = _engine.GetSeriesState(userId, jf);
        return state is not null && state.State != SeriesUserState.NotTracked;
    }

    private static string LabelFor(ComingSoonKind kind, MediaType mediaType, int? season, int? episode, DateTime airUtc)
    {
        var when = RelativeDay(airUtc);
        var prefix = ComingSoonClassifier.LabelPrefix(kind);
        if (mediaType == MediaType.Series && season is { } s && episode is { } e)
        {
            return $"{prefix} · S{s}E{e} · {when}";
        }

        return $"{prefix} · {when}";
    }

    private static string RelativeDay(DateTime airUtc)
    {
        var days = (airUtc.Date - DateTime.UtcNow.Date).Days;
        return days switch
        {
            <= 0 => "Today",
            1 => "Tomorrow",
            _ => airUtc.DayOfWeek.ToString(),
        };
    }

    private sealed record Candidate(CatalogItem Item, DateTime AirUtc, MediaType Type, int? Season, int? Episode);
}
