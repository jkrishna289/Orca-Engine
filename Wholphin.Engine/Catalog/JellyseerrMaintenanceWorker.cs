using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wholphin.Engine.Data;
using Wholphin.Engine.Discovery;

namespace Wholphin.Engine.Catalog;

/// <summary>
/// Periodic background maintenance for the availability-aware catalog (Milestone 2 + 7). On each tick
/// it reconciles in-flight item availability and backfills missing TMDB metadata (genres/artwork) on a
/// batch of requestable rows; every few hours it also runs the taste-driven discovery cycle (sweep →
/// global trending/country pulls → rotated per-user pulls). Every underlying operation is gated +
/// fail-soft, so this is cheap and harmless when Jellyseerr/TMDB are unconfigured or the features are
/// disabled.
/// </summary>
public class JellyseerrMaintenanceWorker : IHostedService
{
    /// <summary>Delay before the first cycle, so the server finishes starting up first.</summary>
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(2);

    /// <summary>How often availability is reconciled.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    /// <summary>Run a discovery cycle every N ticks (≈ every 2 hours at a 15-minute interval).</summary>
    private const int DiscoveryEveryNTicks = 8;

    /// <summary>Profile event count that roughly matches the 0.40 confidence pull gate (30 × 0.40).</summary>
    private const int MinEventsForPull = 12;

    /// <summary>Requestable rows to backfill from TMDB per tick (round-robin; no-op when nothing's missing).</summary>
    private const int EnrichPerTick = 40;

    /// <summary>Rows to tag with a watch-provider brand per tick (round-robin; no-op when nothing's untagged).</summary>
    private const int WatchProvidersPerTick = 40;

    /// <summary>Titles to pre-generate content advisories for per tick (Groq-bound; no-op when nothing's untagged).</summary>
    private const int WarningsPerTick = 25;

    private readonly IAvailabilityReconciler _reconciler;
    private readonly IDiscoveryOrchestrator _discovery;
    private readonly ICatalogEnricher _enricher;
    private readonly IWatchProviderEnricher _watchProviders;
    private readonly Wholphin.Engine.Metadata.IContentWarningEnricher _contentWarnings;
    private readonly Wholphin.Engine.Analytics.ICommunityRatingService _communityRatings;
    private readonly IWholphinDbContextFactory _factory;
    private readonly ILogger<JellyseerrMaintenanceWorker> _logger;

    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyseerrMaintenanceWorker"/> class.
    /// </summary>
    /// <param name="reconciler">The availability reconciler.</param>
    /// <param name="discovery">The taste-driven discovery orchestrator.</param>
    /// <param name="enricher">The TMDB catalog enricher.</param>
    /// <param name="watchProviders">The watch-provider (studio tag) enricher.</param>
    /// <param name="contentWarnings">The content-advisory pre-generator.</param>
    /// <param name="communityRatings">The Wholphin community-rating service.</param>
    /// <param name="factory">Database context factory (per-user pull rotation).</param>
    /// <param name="logger">The logger.</param>
    public JellyseerrMaintenanceWorker(
        IAvailabilityReconciler reconciler,
        IDiscoveryOrchestrator discovery,
        ICatalogEnricher enricher,
        IWatchProviderEnricher watchProviders,
        Wholphin.Engine.Metadata.IContentWarningEnricher contentWarnings,
        Wholphin.Engine.Analytics.ICommunityRatingService communityRatings,
        IWholphinDbContextFactory factory,
        ILogger<JellyseerrMaintenanceWorker> logger)
    {
        _reconciler = reconciler;
        _discovery = discovery;
        _enricher = enricher;
        _watchProviders = watchProviders;
        _contentWarnings = contentWarnings;
        _communityRatings = communityRatings;
        _factory = factory;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _loop = Task.Run(() => RunAsync(_cts.Token), CancellationToken.None);
        _logger.LogInformation("Orca Engine: Jellyseerr maintenance worker started.");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cts.Cancel();
        return Task.CompletedTask;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(InitialDelay, ct).ConfigureAwait(false);

            var tick = 0;
            using var timer = new PeriodicTimer(Interval);
            do
            {
                try
                {
                    await _reconciler.ReconcileAsync(ct: ct).ConfigureAwait(false);
                    await _enricher.EnrichAsync(EnrichPerTick, ct).ConfigureAwait(false);
                    await _watchProviders.EnrichAsync(WatchProvidersPerTick, ct).ConfigureAwait(false);
                    await _contentWarnings.EnrichAsync(WarningsPerTick, ct).ConfigureAwait(false);
                    await _communityRatings.RecomputeAllAsync(ct).ConfigureAwait(false);

                    if (tick % DiscoveryEveryNTicks == 0)
                    {
                        await RunDiscoveryCycleAsync(ct).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Orca Engine: Jellyseerr maintenance cycle failed.");
                }

                tick++;
            }
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    /// <summary>
    /// One taste-driven discovery cycle: sweep (decay + GC) → global trending/country pulls →
    /// per-user pulls, rotated fairly across warm profiles (users whose last run is oldest go
    /// first, capped per cycle so a big household can't starve a tick).
    /// </summary>
    private async Task RunDiscoveryCycleAsync(CancellationToken ct)
    {
        await _discovery.SweepAsync(ct).ConfigureAwait(false);
        await _discovery.PullGlobalAsync(ct).ConfigureAwait(false);

        var tuning = DiscoveryTuning.Resolve();
        await using var db = _factory.Create();

        // Retention: physically prune raw behavior events past the window (the affinity vector has
        // already decayed them to near-zero). No-op when retention is disabled.
        var cutoff = Personalization.PersonalizationService.RetentionCutoff();
        if (cutoff > DateTime.MinValue)
        {
            var pruned = await db.BehaviorEvents
                .Where(e => e.Timestamp < cutoff)
                .ExecuteDeleteAsync(ct)
                .ConfigureAwait(false);
            if (pruned > 0)
            {
                _logger.LogInformation("Orca Engine: pruned {Count} behavior events older than retention.", pruned);
            }
        }

        var eligible = await db.UserProfiles.AsNoTracking()
            .Where(p => p.EventCount >= MinEventsForPull)
            .Select(p => p.UserId)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (eligible.Count == 0)
        {
            return;
        }

        var lastRuns = await db.DiscoveryRuns.AsNoTracking()
            .Where(r => eligible.Contains(r.UserId))
            .GroupBy(r => r.UserId)
            .Select(g => new { UserId = g.Key, LastRun = g.Max(r => r.StartedAt) })
            .ToDictionaryAsync(x => x.UserId, x => x.LastRun, ct)
            .ConfigureAwait(false);

        var rotation = eligible
            .OrderBy(u => lastRuns.TryGetValue(u, out var at) ? at : DateTime.MinValue)
            .Take(tuning.MaxUsersPerCycle);
        foreach (var userId in rotation)
        {
            await _discovery.PullForUserAsync(userId, ct).ConfigureAwait(false);
        }
    }
}
