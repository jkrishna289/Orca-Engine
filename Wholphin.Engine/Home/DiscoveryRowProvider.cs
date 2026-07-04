using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wholphin.Engine.Contracts;
using Wholphin.Engine.Data;
using Wholphin.Engine.Data.Entities;
using Wholphin.Engine.Data.Enums;
using Wholphin.Engine.Discovery;
using Wholphin.Engine.Embedding;
using Wholphin.Engine.Personalization;
using Wholphin.Engine.Recommendation;

namespace Wholphin.Engine.Home;

/// <summary>
/// Builds the justified external-content rows FROM the discovery picks — nothing renders here
/// without a <see cref="UserDiscoveryPick"/> explaining why:
/// <list type="bullet">
///   <item><description><b>trending</b> — global trending picks, blended with the viewer's taste
///   for warm profiles ("a pinch of taste"); falls back to the local catalog top-10 when no picks
///   exist (no TMDB key / feature off) so library-only installs keep the row.</description></item>
///   <item><description><b>youmightlike</b> — the user's taste-match + labeled exploration picks.</description></item>
///   <item><description><b>byw:{seedTmdbId}</b> — up to two pulled "Because You Watched {seed}" rows.</description></item>
///   <item><description><b>country</b> — "What people are watching in {Country}" (per-user country,
///   admin fallback); renders for cold and anonymous users too (justified globally).</description></item>
/// </list>
/// </summary>
public class DiscoveryRowProvider : IRowProvider
{
    private const int Top10RowSize = 10;
    private const int MaxSeedRows = 2;
    private const double WarmConfidence = 0.40;

    private readonly IWholphinDbContextFactory _factory;
    private readonly ITasteProfileService _tasteProfiles;
    private readonly IContentVectorIndex _vectorIndex;
    private readonly ILogger<DiscoveryRowProvider> _logger;

    /// <summary>Initializes a new instance of the <see cref="DiscoveryRowProvider"/> class.</summary>
    /// <param name="factory">Database context factory.</param>
    /// <param name="tasteProfiles">The taste-profile file service (trending taste blend).</param>
    /// <param name="vectorIndex">The content-vector index (render-time same-space cosine).</param>
    /// <param name="logger">The logger.</param>
    public DiscoveryRowProvider(
        IWholphinDbContextFactory factory,
        ITasteProfileService tasteProfiles,
        IContentVectorIndex vectorIndex,
        ILogger<DiscoveryRowProvider> logger)
    {
        _factory = factory;
        _tasteProfiles = tasteProfiles;
        _vectorIndex = vectorIndex;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ProviderRow>> BuildAsync(RowContext context, CancellationToken ct = default)
    {
        var rows = new List<ProviderRow>();
        var flags = context.Settings.Features;
        var now = DateTime.UtcNow;

        await using var db = _factory.Create();

        if (flags.Trending)
        {
            var trending = await BuildTrendingAsync(db, context, now, ct).ConfigureAwait(false);
            if (trending is not null)
            {
                rows.Add(trending);
            }
        }

        if (flags.TasteDiscovery)
        {
            if (context.UserId is { } userId && userId != Guid.Empty)
            {
                rows.AddRange(await BuildPersonalRowsAsync(db, userId, context.RowSize, now, ct).ConfigureAwait(false));
            }

            var country = await BuildCountryRowAsync(db, context, now, ct).ConfigureAwait(false);
            if (country is not null)
            {
                rows.Add(country);
            }
        }

        return rows;
    }

    private async Task<ProviderRow?> BuildTrendingAsync(WholphinDbContext db, RowContext context, DateTime now, CancellationToken ct)
    {
        var picks = await LoadPicksAsync(db, Guid.Empty, DiscoveryPickKind.Trending, country: null, now, ct).ConfigureAwait(false);

        if (picks.Count == 0)
        {
            // No global picks (feature off / no TMDB key): keep the row alive with the local
            // catalog's top-rated titles, exactly as before taste-driven discovery existed.
            var fallback = await db.CatalogItems.AsNoTracking()
                .Where(c => c.CommunityRating != null)
                .OrderByDescending(c => c.CommunityRating)
                .Take(Top10RowSize)
                .ToListAsync(ct)
                .ConfigureAwait(false);
            return fallback.Count == 0 ? null : new ProviderRow
            {
                Id = "trending",
                Title = "Trending",
                Purpose = "trending",
                RowStyle = RowStyle.Top10,
                Items = fallback,
                WarmPriority = 7,
                ColdPriority = 3,
            };
        }

        // "A pinch of the user's taste": warm viewers see trending re-ranked by
        // (1-w)·popularity + w·taste-cosine; cold/anonymous viewers see pure popularity. The
        // cosine is computed inside the CURRENT snapshot via the seed-mean (same-space by
        // construction — trending items are catalog rows present in the snapshot).
        var ranked = picks.OrderByDescending(p => p.Pick.PopularityScore).ToList();
        if (context.UserId is { } userId && userId != Guid.Empty && context.Confidence >= WarmConfidence)
        {
            try
            {
                var profile = await _tasteProfiles.GetAsync(userId, ct).ConfigureAwait(false);
                if (profile is { Seeds.Count: > 0 })
                {
                    var weight = DiscoveryTuning.Resolve().TrendingTasteWeight;
                    var snapshot = await _vectorIndex.GetAsync(ct).ConfigureAwait(false);
                    var userVector = _tasteProfiles.VectorInSnapshotSpace(profile, snapshot);
                    if (!userVector.IsEmpty)
                    {
                        ranked = picks
                            .OrderByDescending(p =>
                                ((1 - weight) * p.Pick.PopularityScore)
                                + (weight * ContentVector.Cosine(userVector, snapshot.VectorFor(p.Item.Id))))
                            .ToList();
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Orca Engine: trending taste blend failed; using pure popularity.");
            }
        }

        return new ProviderRow
        {
            Id = "trending",
            Title = "Trending",
            Purpose = "trending",
            RowStyle = RowStyle.Top10,
            Items = ranked.Select(p => p.Item).Take(Top10RowSize).ToList(),
            WarmPriority = 7,
            ColdPriority = 3,
        };
    }

    private async Task<List<ProviderRow>> BuildPersonalRowsAsync(WholphinDbContext db, Guid userId, int rowSize, DateTime now, CancellationToken ct)
    {
        var rows = new List<ProviderRow>();

        // You Might Like: taste matches plus the honestly-labeled exploration quota, one row.
        var tasteKinds = new[] { DiscoveryPickKind.TasteMatch, DiscoveryPickKind.Exploration };
        var tastePicks = await (
            from pick in db.UserDiscoveryPicks.AsNoTracking()
            join item in db.CatalogItems.AsNoTracking() on pick.CatalogItemId equals item.Id
            where pick.UserId == userId
                && tasteKinds.Contains(pick.Kind)
                && (pick.ExpiresAt == null || pick.ExpiresAt > now)
                && item.Availability == AvailabilityState.Request
            orderby pick.FinalScore descending
            select new PickRow(pick, item))
            .Take(rowSize * 2)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (tastePicks.Count > 0)
        {
            rows.Add(new ProviderRow
            {
                Id = "youmightlike",
                Title = "You Might Like",
                Purpose = "discover",
                Items = tastePicks.Select(p => p.Item).ToList(),
                Reasons = Reasons(tastePicks),
                WarmPriority = 8,
                ColdPriority = 15,
            });
        }

        // Because You Watched {seed}: the two freshest seed groups, one row each.
        var seedPicks = await (
            from pick in db.UserDiscoveryPicks.AsNoTracking()
            join item in db.CatalogItems.AsNoTracking() on pick.CatalogItemId equals item.Id
            where pick.UserId == userId
                && pick.Kind == DiscoveryPickKind.BecauseYouWatched
                && pick.SeedTmdbId != null
                && (pick.ExpiresAt == null || pick.ExpiresAt > now)
                && item.Availability == AvailabilityState.Request
            select new PickRow(pick, item))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var seedGroups = seedPicks
            .GroupBy(p => p.Pick.SeedTmdbId!.Value)
            .OrderByDescending(g => g.Max(p => p.Pick.CreatedAt))
            .Take(MaxSeedRows);
        foreach (var group in seedGroups)
        {
            var ordered = group.OrderByDescending(p => p.Pick.FinalScore).ToList();
            var seedTitle = ordered[0].Pick.SeedTitle;
            rows.Add(new ProviderRow
            {
                Id = $"byw:{group.Key}",
                Title = string.IsNullOrWhiteSpace(seedTitle) ? "Because You Watched" : $"Because You Watched {seedTitle}",
                Purpose = "discover",
                Items = ordered.Select(p => p.Item).ToList(),
                Reasons = Reasons(ordered),
                WarmPriority = 5,
                ColdPriority = 10,
            });
        }

        return rows;
    }

    private async Task<ProviderRow?> BuildCountryRowAsync(WholphinDbContext db, RowContext context, DateTime now, CancellationToken ct)
    {
        var cc = context.Settings.CountryCode;
        var picks = await LoadPicksAsync(db, Guid.Empty, DiscoveryPickKind.Country, cc, now, ct).ConfigureAwait(false);
        if (picks.Count == 0)
        {
            return null;
        }

        return new ProviderRow
        {
            Id = "country",
            Title = $"What people are watching in {SelectionStage.CountryName(cc)}",
            Purpose = "discover",
            Items = picks.OrderByDescending(p => p.Pick.FinalScore).Select(p => p.Item).ToList(),
            WarmPriority = 8,
            ColdPriority = 5,
        };
    }

    private static async Task<List<PickRow>> LoadPicksAsync(
        WholphinDbContext db,
        Guid userId,
        DiscoveryPickKind kind,
        string? country,
        DateTime now,
        CancellationToken ct)
    {
        var query =
            from pick in db.UserDiscoveryPicks.AsNoTracking()
            join item in db.CatalogItems.AsNoTracking() on pick.CatalogItemId equals item.Id
            where pick.UserId == userId
                && pick.Kind == kind
                && (pick.ExpiresAt == null || pick.ExpiresAt > now)
            select new PickRow(pick, item);
        if (country is not null)
        {
            query = query.Where(p => p.Pick.Country == country);
        }

        return await query.ToListAsync(ct).ConfigureAwait(false);
    }

    private static Dictionary<long, string> Reasons(IEnumerable<PickRow> picks)
    {
        var reasons = new Dictionary<long, string>();
        foreach (var p in picks)
        {
            if (!string.IsNullOrWhiteSpace(p.Pick.Reason))
            {
                reasons[p.Item.Id] = p.Pick.Reason!;
            }
        }

        return reasons;
    }

    private sealed record PickRow(UserDiscoveryPick Pick, CatalogItem Item);
}
