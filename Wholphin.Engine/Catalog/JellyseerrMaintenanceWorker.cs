using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    private readonly IReadOnlyList<ICatalogEnricher> _enrichers;
    private readonly IWatchProviderEnricher _watchProviders;
    private readonly Wholphin.Engine.Metadata.IContentWarningEnricher _contentWarnings;
    private readonly Wholphin.Engine.Analytics.ICommunityRatingService _communityRatings;
    private readonly IWholphinDbContextFactory _factory;
    private readonly Diagnostics.IEngineMetrics _metrics;
    private readonly ILogger<JellyseerrMaintenanceWorker> _logger;

    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyseerrMaintenanceWorker"/> class.
    /// </summary>
    /// <param name="reconciler">The availability reconciler.</param>
    /// <param name="discovery">The taste-driven discovery orchestrator.</param>
    /// <param name="enrichers">Every catalog enricher (TMDB backfill + multi-provider), run in registration order.</param>
    /// <param name="watchProviders">The watch-provider (studio tag) enricher.</param>
    /// <param name="contentWarnings">The content-advisory pre-generator.</param>
    /// <param name="communityRatings">The Wholphin community-rating service.</param>
    /// <param name="factory">Database context factory (per-user pull rotation).</param>
    /// <param name="metrics">Operational metrics — the maintenance loop's heartbeat.</param>
    /// <param name="logger">The logger.</param>
    public JellyseerrMaintenanceWorker(
        IAvailabilityReconciler reconciler,
        IDiscoveryOrchestrator discovery,
        IEnumerable<ICatalogEnricher> enrichers,
        IWatchProviderEnricher watchProviders,
        Wholphin.Engine.Metadata.IContentWarningEnricher contentWarnings,
        Wholphin.Engine.Analytics.ICommunityRatingService communityRatings,
        IWholphinDbContextFactory factory,
        Diagnostics.IEngineMetrics metrics,
        ILogger<JellyseerrMaintenanceWorker> logger)
    {
        _reconciler = reconciler;
        _discovery = discovery;
        _enrichers = enrichers.ToList();
        _watchProviders = watchProviders;
        _contentWarnings = contentWarnings;
        _communityRatings = communityRatings;
        _factory = factory;
        _metrics = metrics;
        _logger = logger;
    }

    private static long ElapsedMs(long startedAt) =>
        (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;

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
                // The whole cycle is timed and recorded: this loop is the engine's heartbeat, and
                // until now a tick that failed every time only ever produced a log line — nothing
                // counted it, so silent permanent degradation looked identical to a healthy engine.
                var tickStartedAt = Stopwatch.GetTimestamp();
                try
                {
                    await _reconciler.ReconcileAsync(ct: ct).ConfigureAwait(false);
                    foreach (var enricher in _enrichers)
                    {
                        await enricher.EnrichAsync(EnrichPerTick, ct).ConfigureAwait(false);
                    }
                    await _watchProviders.EnrichAsync(WatchProvidersPerTick, ct).ConfigureAwait(false);
                    await _contentWarnings.EnrichAsync(WarningsPerTick, ct).ConfigureAwait(false);
                    await _communityRatings.RecomputeAllAsync(ct).ConfigureAwait(false);

                    if (tick % DiscoveryEveryNTicks == 0)
                    {
                        await RunDiscoveryCycleAsync(ct).ConfigureAwait(false);
                    }

                    _metrics.Record("maintenance.tick", ElapsedMs(tickStartedAt), data: $"tick {tick}");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _metrics.Increment("maintenance.tick.error");
                    _metrics.Record("maintenance.tick", ElapsedMs(tickStartedAt), ok: false, data: $"tick {tick}", exception: ex);
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
