using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wholphin.Engine.Data;
using Wholphin.Engine.Data.Enums;
using Wholphin.Engine.Integrations.Jellyseerr;

namespace Wholphin.Engine.Catalog;

/// <summary>
/// Default <see cref="IAvailabilityReconciler"/>. Polls Jellyseerr (read-only) for items the engine
/// is tracking as <see cref="AvailabilityState.Requested"/> or <see cref="AvailabilityState.Downloading"/>
/// and advances them through the availability state machine.
/// </summary>
public class AvailabilityReconciler : IAvailabilityReconciler
{
    private readonly IJellyseerrClient _jellyseerr;
    private readonly IWholphinDbContextFactory _factory;
    private readonly ILogger<AvailabilityReconciler> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AvailabilityReconciler"/> class.
    /// </summary>
    /// <param name="jellyseerr">The Jellyseerr client.</param>
    /// <param name="factory">The database context factory.</param>
    /// <param name="logger">The logger.</param>
    public AvailabilityReconciler(
        IJellyseerrClient jellyseerr,
        IWholphinDbContextFactory factory,
        ILogger<AvailabilityReconciler> logger)
    {
        _jellyseerr = jellyseerr;
        _factory = factory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<int> ReconcileAsync(int maxItems = 200, CancellationToken ct = default)
    {
        if (!_jellyseerr.IsConfigured)
        {
            return 0;
        }

        await using var db = _factory.Create();

        // Only the in-flight states need polling; oldest-synced first so passes round-robin.
        var pending = await db.CatalogItems
            .Where(c => c.TmdbId != null
                        && (c.Availability == AvailabilityState.Requested || c.Availability == AvailabilityState.Downloading))
            .OrderBy(c => c.LastSyncedAt)
            .Take(Math.Clamp(maxItems, 1, 1000))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (pending.Count == 0)
        {
            return 0;
        }

        var updated = 0;
        foreach (var item in pending)
        {
            ct.ThrowIfCancellationRequested();

            var availability = await _jellyseerr
                .GetAvailabilityAsync(item.TmdbId!.Value, item.MediaType, ct)
                .ConfigureAwait(false);
            if (availability is not { } resolved)
            {
                // Unknown/unreachable — leave the state as-is, just touch so the pass rotates.
                item.LastSyncedAt = DateTime.UtcNow;
                continue;
            }

            // Jellyseerr reports "available" the moment the file lands, but the item is only truly
            // playable once the library sync attaches a Jellyfin id — until then it's RecentlyAdded.
            if (resolved == AvailabilityState.WatchNow && item.JellyfinItemId is null)
            {
                resolved = AvailabilityState.RecentlyAdded;
            }

            if (resolved != item.Availability)
            {
                item.Availability = resolved;
                updated++;
            }

            item.LastSyncedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("Orca Engine: availability reconcile updated {Updated}/{Total} items.", updated, pending.Count);
        return updated;
    }
}
