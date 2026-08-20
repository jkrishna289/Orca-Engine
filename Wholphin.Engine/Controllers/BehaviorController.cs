using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wholphin.Engine.Behavior;
using Wholphin.Engine.Data;
using Wholphin.Engine.Data.Entities;
using Wholphin.Engine.Data.Enums;
using Wholphin.Engine.Discovery;

namespace Wholphin.Engine.Controllers;

/// <summary>
/// Explicit feedback ingestion (thumbs up/down) plus behavior-log inspection (dev/verification).
/// </summary>
[ApiController]
[Route("OrcaEngine/Behavior")]
[Produces("application/json")]
public class BehaviorController : ControllerBase
{
    private static readonly HashSet<BehaviorEventType> AllowedTelemetry = new()
    {
        BehaviorEventType.CardImpression,
        BehaviorEventType.CardFocused,
        BehaviorEventType.CardClicked,
        BehaviorEventType.TrailerPlayed,
        BehaviorEventType.Browsed,
        BehaviorEventType.Searched,
    };

    private readonly IBehaviorService _behavior;
    private readonly IWholphinDbContextFactory _factory;
    private readonly IWatchHistoryImporter _historyImporter;

    /// <summary>
    /// Initializes a new instance of the <see cref="BehaviorController"/> class.
    /// </summary>
    /// <param name="behavior">The behavior capture service.</param>
    /// <param name="factory">Database context factory (external-item attribution + memory).</param>
    /// <param name="historyImporter">One-time backfill of Jellyfin's existing watch history.</param>
    public BehaviorController(
        IBehaviorService behavior,
        IWholphinDbContextFactory factory,
        IWatchHistoryImporter historyImporter)
    {
        _behavior = behavior;
        _factory = factory;
        _historyImporter = historyImporter;
    }

    /// <summary>
    /// Starts the one-time watch-history import: reads every user's existing Jellyfin played /
    /// favorite / rating state and backfills the behavior log from it.
    /// </summary>
    /// <returns>202 with the initial progress, or 409 when a run is already in flight.</returns>
    /// <remarks>
    /// Returns immediately — the run happens in the background and is followed with
    /// <see cref="GetImportHistory"/>. Safe to re-run: each run replaces its own previous rows and
    /// never touches live-captured events.
    /// </remarks>
    [HttpPost("ImportHistory")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult<HistoryImportProgress> StartImportHistory()
    {
        if (!_historyImporter.TryStart())
        {
            return Conflict(_historyImporter.Progress);
        }

        return Accepted(_historyImporter.Progress);
    }

    /// <summary>Returns the live progress of the watch-history import, per user.</summary>
    /// <returns>The progress snapshot.</returns>
    [HttpGet("ImportHistory")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult<HistoryImportProgress> GetImportHistory() => Ok(_historyImporter.Progress);

    /// <summary>Records an explicit thumbs-up / thumbs-down for an item.</summary>
    /// <param name="request">The feedback request.</param>
    /// <returns>Acceptance result.</returns>
    [HttpPost("Feedback")]
    [AllowAnonymous]
    public ActionResult RecordFeedback([FromBody] FeedbackRequest request)
    {
        if (request.UserId == Guid.Empty || request.ItemId == Guid.Empty)
        {
            return BadRequest(new { error = "UserId and ItemId are required." });
        }

        _behavior.Record(new BehaviorEvent
        {
            UserId = request.UserId,
            JellyfinItemId = request.ItemId,
            EventType = request.ThumbsUp ? BehaviorEventType.ThumbsUp : BehaviorEventType.ThumbsDown,
            Value = request.ThumbsUp ? 1 : -1
        });

        return Accepted(new { status = "recorded", signal = request.ThumbsUp ? "ThumbsUp" : "ThumbsDown" });
    }

    /// <summary>
    /// Batch-ingests home telemetry (impressions, focus/dwell, clicks, trailer plays). Batched so
    /// the client can flush many cards in one round-trip. Only telemetry event types are accepted;
    /// anything else is ignored (explicit feedback goes through <see cref="RecordFeedback"/>).
    /// External (not-in-library) cards have no Jellyfin id — they attribute via
    /// <see cref="TelemetryEvent.TmdbId"/> instead, which resolves to the catalog row and feeds
    /// both taste learning and per-item recommendation memory.
    /// </summary>
    /// <param name="batch">The telemetry batch.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many events were accepted.</returns>
    [HttpPost("Events")]
    [AllowAnonymous]
    public async Task<ActionResult> RecordEvents([FromBody] TelemetryBatch batch, CancellationToken cancellationToken)
    {
        if (batch.UserId == Guid.Empty || batch.Events is not { Count: > 0 })
        {
            return BadRequest(new { error = "UserId and at least one event are required." });
        }

        // Resolve external-card TMDB ids to catalog rows once for the whole batch.
        var externalIds = batch.Events
            .Where(e => e.ItemId == Guid.Empty && e.TmdbId is > 0)
            .Select(e => e.TmdbId!.Value)
            .Distinct()
            .ToList();
        var externalRows = new Dictionary<int, CatalogItem>();
        if (externalIds.Count > 0)
        {
            await using var db = _factory.Create();
            var rows = await db.CatalogItems.AsNoTracking()
                .Where(c => c.TmdbId != null && externalIds.Contains(c.TmdbId.Value))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            foreach (var row in rows)
            {
                externalRows.TryAdd(row.TmdbId!.Value, row);
            }
        }

        var accepted = 0;
        var memoryUpdates = new List<(CatalogItem Item, BehaviorEventType Type)>();
        foreach (var ev in batch.Events)
        {
            if (!Enum.TryParse<BehaviorEventType>(ev.EventType, ignoreCase: true, out var type) || !AllowedTelemetry.Contains(type))
            {
                continue;
            }

            CatalogItem? external = null;
            if (ev.ItemId == Guid.Empty && ev.TmdbId is { } tmdbId && tmdbId > 0)
            {
                // Unknown TMDB ids are dropped fail-soft: an event we can't attribute teaches nothing.
                if (!externalRows.TryGetValue(tmdbId, out external))
                {
                    continue;
                }
            }

            _behavior.Record(new BehaviorEvent
            {
                UserId = batch.UserId,
                JellyfinItemId = ev.ItemId == Guid.Empty ? null : ev.ItemId,
                CatalogItemId = external?.Id,
                EventType = type,
                Value = ev.Value,
                ContextJson = ev.Context,
            });
            accepted++;

            if (external is not null && type is BehaviorEventType.CardImpression
                or BehaviorEventType.CardClicked
                or BehaviorEventType.TrailerPlayed)
            {
                memoryUpdates.Add((external, type));
            }
        }

        if (memoryUpdates.Count > 0)
        {
            await ApplyMemoryAsync(batch.UserId, memoryUpdates, cancellationToken).ConfigureAwait(false);
        }

        return Accepted(new { accepted });
    }

    /// <summary>
    /// Feeds external-card engagement into recommendation memory: impressions count exposure,
    /// clicks and trailer plays restore interest (the gradual counterpart of the ignored-cycle
    /// decay applied by the discovery sweep).
    /// </summary>
    private async Task ApplyMemoryAsync(
        Guid userId,
        IReadOnlyList<(CatalogItem Item, BehaviorEventType Type)> updates,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        await using var db = _factory.Create();
        var tmdbIds = updates.Select(u => u.Item.TmdbId!.Value).Distinct().ToList();
        var memories = await db.UserItemMemories
            .Where(m => m.UserId == userId && tmdbIds.Contains(m.TmdbId))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var byKey = memories.ToDictionary(m => (m.TmdbId, m.MediaType), m => m);

        foreach (var (item, type) in updates)
        {
            var key = (item.TmdbId!.Value, item.MediaType);
            if (!byKey.TryGetValue(key, out var memory))
            {
                memory = new UserItemMemory
                {
                    UserId = userId,
                    TmdbId = key.Item1,
                    MediaType = key.Item2,
                    InterestScore = 1.0,
                    UpdatedAt = now,
                };
                db.UserItemMemories.Add(memory);
                byKey[key] = memory;
            }

            if (type == BehaviorEventType.CardImpression)
            {
                InterestModel.OnImpression(memory, now);
            }
            else
            {
                InterestModel.OnEngaged(memory, type, now);
            }
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Returns the most recent behavior events (newest first).</summary>
    /// <param name="userId">Optional user filter.</param>
    /// <param name="limit">Maximum rows (1-500).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The recent events.</returns>
    [HttpGet("Recent")]
    [AllowAnonymous]
    public async Task<ActionResult<IReadOnlyList<BehaviorEventDto>>> GetRecent(
        [FromQuery] Guid? userId,
        [FromQuery] int limit,
        CancellationToken cancellationToken)
    {
        var events = await _behavior.GetRecentAsync(userId, limit <= 0 ? 50 : limit, cancellationToken).ConfigureAwait(false);
        return events.Select(BehaviorEventDto.From).ToList();
    }

    /// <summary>Returns the behavior-log event count, optionally per user.</summary>
    /// <param name="userId">Optional user filter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The count.</returns>
    [HttpGet("Stats")]
    [AllowAnonymous]
    public async Task<ActionResult<object>> GetStats([FromQuery] Guid? userId, CancellationToken cancellationToken)
    {
        var count = await _behavior.CountAsync(userId, cancellationToken).ConfigureAwait(false);
        return new { Events = count, UserId = userId };
    }

    /// <summary>Explicit-feedback request body.</summary>
    public class FeedbackRequest
    {
        /// <summary>Gets or sets the Jellyfin user id.</summary>
        public Guid UserId { get; set; }

        /// <summary>Gets or sets the Jellyfin item id.</summary>
        public Guid ItemId { get; set; }

        /// <summary>Gets or sets a value indicating whether this is a thumbs-up (false = thumbs-down).</summary>
        public bool ThumbsUp { get; set; }
    }

    /// <summary>A batch of home-telemetry events for one user.</summary>
    public class TelemetryBatch
    {
        /// <summary>Gets or sets the Jellyfin user id.</summary>
        public Guid UserId { get; set; }

        /// <summary>Gets or sets the events in this batch.</summary>
        public List<TelemetryEvent> Events { get; set; } = new();
    }

    /// <summary>A single telemetry event (impression / focus / click / trailer / browse / search).</summary>
    public class TelemetryEvent
    {
        /// <summary>Gets or sets the Jellyfin item id (empty for non-item events like search, and for external cards).</summary>
        public Guid ItemId { get; set; }

        /// <summary>
        /// Gets or sets the TMDB id for external (not-in-library) cards, which have no Jellyfin id.
        /// Used only when <see cref="ItemId"/> is empty; unknown ids are dropped. Optional —
        /// older clients that never send it keep working unchanged.
        /// </summary>
        public int? TmdbId { get; set; }

        /// <summary>Gets or sets the event type name (CardImpression / CardFocused / CardClicked / TrailerPlayed / Browsed / Searched).</summary>
        public string EventType { get; set; } = string.Empty;

        /// <summary>Gets or sets the value (e.g. focus/dwell seconds for CardFocused).</summary>
        public double Value { get; set; }

        /// <summary>Gets or sets optional context JSON (hour, row, position, …).</summary>
        public string? Context { get; set; }
    }

    /// <summary>Read-model projection of a behavior event.</summary>
    public class BehaviorEventDto
    {
        /// <summary>Gets or sets the event id.</summary>
        public long Id { get; set; }

        /// <summary>Gets or sets the user id.</summary>
        public Guid UserId { get; set; }

        /// <summary>Gets or sets the Jellyfin item id.</summary>
        public Guid? ItemId { get; set; }

        /// <summary>Gets or sets the event type name.</summary>
        public string EventType { get; set; } = string.Empty;

        /// <summary>Gets or sets the value.</summary>
        public double Value { get; set; }

        /// <summary>Gets or sets the affinity weight this signal carries.</summary>
        public double Weight { get; set; }

        /// <summary>Gets or sets the timestamp (UTC).</summary>
        public DateTime Timestamp { get; set; }

        /// <summary>Gets or sets the context JSON.</summary>
        public string? Context { get; set; }

        /// <summary>Maps an entity to its DTO.</summary>
        /// <param name="e">The entity.</param>
        /// <returns>The DTO.</returns>
        public static BehaviorEventDto From(BehaviorEvent e) => new()
        {
            Id = e.Id,
            UserId = e.UserId,
            ItemId = e.JellyfinItemId,
            EventType = e.EventType.ToString(),
            Value = e.Value,
            Weight = SignalWeights.For(e.EventType, e.Value),
            Timestamp = e.Timestamp,
            Context = e.ContextJson
        };
    }
}
