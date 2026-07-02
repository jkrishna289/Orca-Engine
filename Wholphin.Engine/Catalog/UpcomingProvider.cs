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

namespace Wholphin.Engine.Catalog;

/// <summary>
/// Default <see cref="IUpcomingProvider"/>. Pulls the *arr calendar for the next few weeks and maps
/// entries to catalog items by TMDB id; for catalog series the *arr calendar doesn't cover, it falls
/// back to TMDB's <c>next_episode_to_air</c> (bounded). Result is cached for 12 hours.
/// </summary>
public class UpcomingProvider : IUpcomingProvider
{
    private const string CacheKey = "upcoming";
    private const int UpcomingDays = 21;
    private const int MaxResults = 40;
    private const int MaxTmdbFallback = 25;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(12);

    private readonly IArrClient _arr;
    private readonly ITmdbClient _tmdb;
    private readonly IWholphinDbContextFactory _factory;
    private readonly ICache _cache;
    private readonly ILogger<UpcomingProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="UpcomingProvider"/> class.
    /// </summary>
    public UpcomingProvider(IArrClient arr, ITmdbClient tmdb, IWholphinDbContextFactory factory, ICache cache, ILogger<UpcomingProvider> logger)
    {
        _arr = arr;
        _tmdb = tmdb;
        _factory = factory;
        _cache = cache;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<UpcomingItem>> GetUpcomingAsync(int limit, CancellationToken ct = default)
    {
        if (limit <= 0)
        {
            return Array.Empty<UpcomingItem>();
        }

        if (_cache.TryGet<List<UpcomingItem>>(CacheKey, out var cached) && cached is not null)
        {
            return cached.Take(limit).ToList();
        }

        var built = await BuildAsync(ct).ConfigureAwait(false);
        _cache.Set(CacheKey, built, CacheTtl);
        return built.Take(limit).ToList();
    }

    private async Task<List<UpcomingItem>> BuildAsync(CancellationToken ct)
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

        var picks = new Dictionary<long, UpcomingItem>();

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
                    if (!picks.ContainsKey(item.Id))
                    {
                        picks[item.Id] = new UpcomingItem(item, e.AirDateUtc, LabelFor(e.MediaType, e.SeasonNumber, e.EpisodeNumber, e.AirDateUtc));
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
                .Where(c => c.MediaType == MediaType.Series && !picks.ContainsKey(c.Id))
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

                picks[item.Id] = new UpcomingItem(item, next.AirDate, LabelFor(MediaType.Series, next.SeasonNumber, next.EpisodeNumber, next.AirDate));
            }
        }

        return picks.Values
            .OrderBy(u => u.AirDateUtc)
            .Take(MaxResults)
            .ToList();
    }

    private static string LabelFor(MediaType mediaType, int? season, int? episode, DateTime airUtc)
    {
        var when = RelativeDay(airUtc);
        if (mediaType == MediaType.Series && season is { } s && episode is { } e)
        {
            return $"S{s}E{e} · {when}";
        }

        return when;
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
}
