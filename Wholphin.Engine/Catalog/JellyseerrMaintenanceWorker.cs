using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Wholphin.Engine.Catalog;

/// <summary>
/// Periodic background maintenance for the availability-aware catalog (Milestone 2 + 7). On each tick
/// it reconciles in-flight item availability and backfills missing TMDB metadata (genres/artwork) on a
/// batch of requestable rows; every few hours it also refreshes discovery imports. Every underlying
/// operation is gated + fail-soft, so this is cheap and harmless when Jellyseerr/TMDB are unconfigured
/// or the features are disabled.
/// </summary>
public class JellyseerrMaintenanceWorker : IHostedService
{
    /// <summary>Delay before the first cycle, so the server finishes starting up first.</summary>
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(2);

    /// <summary>How often availability is reconciled.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    /// <summary>Run a discovery import every N ticks (≈ every 2 hours at a 15-minute interval).</summary>
    private const int DiscoveryEveryNTicks = 8;

    /// <summary>
    /// Discovery pages pulled per media type on each discovery cycle. Pulled up so the catalog holds
    /// a deep pool of requestable titles (≈20 items/page × movies+series × sources) — enough to fill
    /// the several "… to Request" rows with fresh, varied content instead of repeating library items.
    /// </summary>
    private const int DiscoveryPages = 10;

    /// <summary>Requestable rows to backfill from TMDB per tick (round-robin; no-op when nothing's missing).</summary>
    private const int EnrichPerTick = 40;

    /// <summary>Rows to tag with a watch-provider brand per tick (round-robin; no-op when nothing's untagged).</summary>
    private const int WatchProvidersPerTick = 40;

    /// <summary>Titles to pre-generate content advisories for per tick (Groq-bound; no-op when nothing's untagged).</summary>
    private const int WarningsPerTick = 25;

    private readonly IAvailabilityReconciler _reconciler;
    private readonly IDiscoveryImporter _discovery;
    private readonly ICatalogEnricher _enricher;
    private readonly IWatchProviderEnricher _watchProviders;
    private readonly Wholphin.Engine.Metadata.IContentWarningEnricher _contentWarnings;
    private readonly Wholphin.Engine.Analytics.ICommunityRatingService _communityRatings;
    private readonly ILogger<JellyseerrMaintenanceWorker> _logger;

    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyseerrMaintenanceWorker"/> class.
    /// </summary>
    /// <param name="reconciler">The availability reconciler.</param>
    /// <param name="discovery">The discovery importer.</param>
    /// <param name="enricher">The TMDB catalog enricher.</param>
    /// <param name="watchProviders">The watch-provider (studio tag) enricher.</param>
    /// <param name="contentWarnings">The content-advisory pre-generator.</param>
    /// <param name="communityRatings">The Wholphin community-rating service.</param>
    /// <param name="logger">The logger.</param>
    public JellyseerrMaintenanceWorker(
        IAvailabilityReconciler reconciler,
        IDiscoveryImporter discovery,
        ICatalogEnricher enricher,
        IWatchProviderEnricher watchProviders,
        Wholphin.Engine.Metadata.IContentWarningEnricher contentWarnings,
        Wholphin.Engine.Analytics.ICommunityRatingService communityRatings,
        ILogger<JellyseerrMaintenanceWorker> logger)
    {
        _reconciler = reconciler;
        _discovery = discovery;
        _enricher = enricher;
        _watchProviders = watchProviders;
        _contentWarnings = contentWarnings;
        _communityRatings = communityRatings;
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
                        await _discovery.ImportAsync(DiscoveryPages, ct).ConfigureAwait(false);
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
}
