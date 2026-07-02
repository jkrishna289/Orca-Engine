using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Wholphin.Engine.Caching;
using Wholphin.Engine.Data;
using Wholphin.Engine.Data.Enums;

namespace Wholphin.Engine.Analytics;

/// <summary>
/// Default <see cref="IFunnelAnalytics"/>. Aggregates the behavior log per item across the funnel
/// stages and derives transition rates. Cached briefly so repeated dashboard refreshes are cheap.
/// </summary>
public class FunnelAnalytics : IFunnelAnalytics
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(2);

    private readonly IWholphinDbContextFactory _factory;
    private readonly ICache _cache;

    /// <summary>
    /// Initializes a new instance of the <see cref="FunnelAnalytics"/> class.
    /// </summary>
    /// <param name="factory">The database context factory.</param>
    /// <param name="cache">The L1 cache.</param>
    public FunnelAnalytics(IWholphinDbContextFactory factory, ICache cache)
    {
        _factory = factory;
        _cache = cache;
    }

    /// <inheritdoc />
    public async Task<FunnelReport> BuildAsync(int limit, CancellationToken ct = default)
    {
        var take = Math.Clamp(limit, 1, 200);
        var cacheKey = $"funnel:{take}";
        if (_cache.TryGet<FunnelReport>(cacheKey, out var cached) && cached is not null)
        {
            return cached;
        }

        await using var db = _factory.Create();

        // Pull the funnel-relevant events (small log; group in memory to avoid EF translation edges).
        var events = await db.BehaviorEvents.AsNoTracking()
            .Where(e => e.JellyfinItemId != null)
            .Select(e => new { ItemId = e.JellyfinItemId!.Value, e.EventType })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var byItem = new Dictionary<Guid, Counts>();
        foreach (var e in events)
        {
            if (!Stage(e.EventType, out var stage))
            {
                continue;
            }

            if (!byItem.TryGetValue(e.ItemId, out var c))
            {
                c = new Counts();
                byItem[e.ItemId] = c;
            }

            c.Add(stage);
        }

        // Resolve titles for the items we'll surface.
        var topIds = byItem
            .OrderByDescending(kv => kv.Value.Shown)
            .ThenByDescending(kv => kv.Value.Played)
            .Take(take)
            .Select(kv => kv.Key)
            .ToList();

        var titles = await db.CatalogItems.AsNoTracking()
            .Where(c => c.JellyfinItemId != null && topIds.Contains(c.JellyfinItemId.Value))
            .Select(c => new { Id = c.JellyfinItemId!.Value, c.Title })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var titleById = titles
            .GroupBy(t => t.Id)
            .ToDictionary(g => g.Key, g => g.First().Title);

        var items = topIds
            .Select(id => ToItemFunnel(id, byItem[id], titleById.GetValueOrDefault(id)))
            .ToList();

        var totals = new Counts();
        foreach (var c in byItem.Values)
        {
            totals.Shown += c.Shown;
            totals.Focused += c.Focused;
            totals.Clicked += c.Clicked;
            totals.Played += c.Played;
            totals.Completed += c.Completed;
        }

        var report = new FunnelReport(
            new FunnelSummary(
                totals.Shown, totals.Focused, totals.Clicked, totals.Played, totals.Completed,
                Rate(totals.Focused, totals.Shown),
                Rate(totals.Clicked, totals.Shown),
                Rate(totals.Played, totals.Clicked),
                Rate(totals.Completed, totals.Played)),
            items);

        _cache.Set(cacheKey, report, CacheTtl);
        return report;
    }

    private static ItemFunnel ToItemFunnel(Guid id, Counts c, string? title) => new(
        id, title, c.Shown, c.Focused, c.Clicked, c.Played, c.Completed,
        Rate(c.Focused, c.Shown),
        Rate(c.Clicked, c.Shown),
        Rate(c.Played, c.Clicked),
        Rate(c.Completed, c.Played));

    private static double Rate(int numerator, int denominator) =>
        denominator <= 0 ? 0 : Math.Round(numerator / (double)denominator, 4);

    private static bool Stage(BehaviorEventType type, out FunnelStage stage)
    {
        switch (type)
        {
            case BehaviorEventType.CardImpression: stage = FunnelStage.Shown; return true;
            case BehaviorEventType.CardFocused: stage = FunnelStage.Focused; return true;
            case BehaviorEventType.CardClicked: stage = FunnelStage.Clicked; return true;
            case BehaviorEventType.PlaybackStarted: stage = FunnelStage.Played; return true;
            case BehaviorEventType.PlaybackCompleted: stage = FunnelStage.Completed; return true;
            default: stage = default; return false;
        }
    }

    private enum FunnelStage
    {
        Shown,
        Focused,
        Clicked,
        Played,
        Completed,
    }

    private sealed class Counts
    {
        public int Shown;
        public int Focused;
        public int Clicked;
        public int Played;
        public int Completed;

        public void Add(FunnelStage stage)
        {
            switch (stage)
            {
                case FunnelStage.Shown: Shown++; break;
                case FunnelStage.Focused: Focused++; break;
                case FunnelStage.Clicked: Clicked++; break;
                case FunnelStage.Played: Played++; break;
                case FunnelStage.Completed: Completed++; break;
            }
        }
    }
}
