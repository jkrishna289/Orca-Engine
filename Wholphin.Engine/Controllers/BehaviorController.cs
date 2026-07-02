using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Wholphin.Engine.Behavior;
using Wholphin.Engine.Data.Entities;
using Wholphin.Engine.Data.Enums;

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
        BehaviorEventType.Browsed,
        BehaviorEventType.Searched,
    };

    private readonly IBehaviorService _behavior;

    /// <summary>
    /// Initializes a new instance of the <see cref="BehaviorController"/> class.
    /// </summary>
    /// <param name="behavior">The behavior capture service.</param>
    public BehaviorController(IBehaviorService behavior) => _behavior = behavior;

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
    /// Batch-ingests home telemetry (impressions, focus/dwell, clicks). Batched so the client can
    /// flush many cards in one round-trip. Only telemetry event types are accepted; anything else is
    /// ignored (explicit feedback goes through <see cref="RecordFeedback"/>).
    /// </summary>
    /// <param name="batch">The telemetry batch.</param>
    /// <returns>How many events were accepted.</returns>
    [HttpPost("Events")]
    [AllowAnonymous]
    public ActionResult RecordEvents([FromBody] TelemetryBatch batch)
    {
        if (batch.UserId == Guid.Empty || batch.Events is not { Count: > 0 })
        {
            return BadRequest(new { error = "UserId and at least one event are required." });
        }

        var accepted = 0;
        foreach (var ev in batch.Events)
        {
            if (!Enum.TryParse<BehaviorEventType>(ev.EventType, ignoreCase: true, out var type) || !AllowedTelemetry.Contains(type))
            {
                continue;
            }

            _behavior.Record(new BehaviorEvent
            {
                UserId = batch.UserId,
                JellyfinItemId = ev.ItemId == Guid.Empty ? null : ev.ItemId,
                EventType = type,
                Value = ev.Value,
                ContextJson = ev.Context,
            });
            accepted++;
        }

        return Accepted(new { accepted });
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

    /// <summary>A single telemetry event (impression / focus / click / browse / search).</summary>
    public class TelemetryEvent
    {
        /// <summary>Gets or sets the Jellyfin item id (empty for non-item events like search).</summary>
        public Guid ItemId { get; set; }

        /// <summary>Gets or sets the event type name (CardImpression / CardFocused / CardClicked / Browsed / Searched).</summary>
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
