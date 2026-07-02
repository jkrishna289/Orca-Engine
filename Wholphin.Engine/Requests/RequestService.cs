using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wholphin.Engine.Behavior;
using Wholphin.Engine.Data;
using Wholphin.Engine.Data.Entities;
using Wholphin.Engine.Data.Enums;
using Wholphin.Engine.Integrations.Jellyseerr;
using Wholphin.Engine.Settings;

namespace Wholphin.Engine.Requests;

/// <summary>
/// Default <see cref="IRequestService"/>. Proxies the request to Jellyseerr, then — on success —
/// records a <see cref="BehaviorEventType.RequestCreated"/> signal (which feeds request affinity)
/// and marks the catalog item <see cref="AvailabilityState.Requested"/>.
/// </summary>
public class RequestService : IRequestService
{
    private readonly IJellyseerrClient _jellyseerr;
    private readonly IBehaviorService _behavior;
    private readonly ISettingsService _settings;
    private readonly IWholphinDbContextFactory _factory;
    private readonly Diagnostics.IEngineMetrics _metrics;
    private readonly ILogger<RequestService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RequestService"/> class.
    /// </summary>
    /// <param name="jellyseerr">The Jellyseerr client.</param>
    /// <param name="behavior">The behavior capture service.</param>
    /// <param name="settings">The layered settings resolver.</param>
    /// <param name="factory">The database context factory.</param>
    /// <param name="metrics">Operational metrics.</param>
    /// <param name="logger">The logger.</param>
    public RequestService(
        IJellyseerrClient jellyseerr,
        IBehaviorService behavior,
        ISettingsService settings,
        IWholphinDbContextFactory factory,
        Diagnostics.IEngineMetrics metrics,
        ILogger<RequestService> logger)
    {
        _jellyseerr = jellyseerr;
        _behavior = behavior;
        _settings = settings;
        _factory = factory;
        _metrics = metrics;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<RequestResult> RequestAsync(Guid userId, int tmdbId, MediaType mediaType, string? title, CancellationToken ct = default)
    {
        if (userId == Guid.Empty || tmdbId <= 0 || mediaType is not (MediaType.Movie or MediaType.Series))
        {
            return new RequestResult { Outcome = RequestOutcome.InvalidInput, Message = "A user id, positive TMDB id, and Movie/Series type are required." };
        }

        var settings = await _settings.ResolveAsync(userId, ct).ConfigureAwait(false);
        if (!settings.Features.Requests)
        {
            return new RequestResult { Outcome = RequestOutcome.Disabled, Message = "Engine-proxied requests are disabled." };
        }

        if (!_jellyseerr.IsConfigured)
        {
            return new RequestResult { Outcome = RequestOutcome.NotConfigured, Message = "Jellyseerr is not configured." };
        }

        var accepted = await _jellyseerr.RequestAsync(tmdbId, mediaType, userId, ct).ConfigureAwait(false);
        if (!accepted)
        {
            return new RequestResult { Outcome = RequestOutcome.Failed, Message = "Jellyseerr did not accept the request." };
        }

        // Reflect the new state in the catalog and capture the request as an affinity signal.
        var catalogId = await UpsertRequestedAsync(tmdbId, mediaType, title, ct).ConfigureAwait(false);

        _behavior.Record(new BehaviorEvent
        {
            UserId = userId,
            JellyfinItemId = null,
            CatalogItemId = catalogId,
            EventType = BehaviorEventType.RequestCreated,
            Value = 1,
            ContextJson = JsonSerializer.Serialize(new { tmdbId, mediaType = mediaType.ToString() }),
        });

        await LogRequestAsync(userId, tmdbId, mediaType, title, ct).ConfigureAwait(false);

        _metrics.Increment("requests.success");
        _logger.LogInformation("Orca Engine: proxied request for {MediaType} {TmdbId} by {UserId}.", mediaType, tmdbId, userId);
        return new RequestResult { Outcome = RequestOutcome.Success, Availability = AvailabilityState.Requested, Message = "Requested." };
    }

    /// <summary>Appends a durable request-log row (best-effort; never fails the request).</summary>
    private async Task LogRequestAsync(Guid userId, int tmdbId, MediaType mediaType, string? title, CancellationToken ct)
    {
        try
        {
            await using var db = _factory.Create();
            db.MediaRequests.Add(new MediaRequest
            {
                UserId = userId,
                TmdbId = tmdbId,
                MediaType = mediaType,
                Title = title,
                Availability = AvailabilityState.Requested,
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Orca Engine: failed to write request-log row for {MediaType} {TmdbId}.", mediaType, tmdbId);
        }
    }

    /// <summary>
    /// Upserts the requestable catalog row to <see cref="AvailabilityState.Requested"/> without
    /// clobbering a richer existing row, and returns its id (for the affinity event).
    /// </summary>
    private async Task<long?> UpsertRequestedAsync(int tmdbId, MediaType mediaType, string? title, CancellationToken ct)
    {
        try
        {
            await using var db = _factory.Create();
            var existing = await db.CatalogItems
                .FirstOrDefaultAsync(c => c.TmdbId == tmdbId && c.MediaType == mediaType, ct)
                .ConfigureAwait(false);

            if (existing is null)
            {
                var row = new CatalogItem
                {
                    TmdbId = tmdbId,
                    MediaType = mediaType,
                    Availability = AvailabilityState.Requested,
                    Title = title ?? string.Empty,
                    LastSyncedAt = DateTime.UtcNow,
                };
                db.CatalogItems.Add(row);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                return row.Id;
            }

            // Never downgrade an already-available/downloading item back to "Requested".
            if (existing.Availability is AvailabilityState.Request or AvailabilityState.Unavailable)
            {
                existing.Availability = AvailabilityState.Requested;
            }

            if (string.IsNullOrWhiteSpace(existing.Title) && !string.IsNullOrWhiteSpace(title))
            {
                existing.Title = title;
            }

            existing.LastSyncedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return existing.Id;
        }
        catch (Exception ex)
        {
            // The request already succeeded in Jellyseerr; a catalog write failure must not fail it.
            _logger.LogWarning(ex, "Orca Engine: failed to reflect request for {MediaType} {TmdbId} in the catalog.", mediaType, tmdbId);
            return null;
        }
    }
}
