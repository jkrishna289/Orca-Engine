using System;
using Wholphin.Engine.Data.Enums;

namespace Wholphin.Engine.Data.Entities;

/// <summary>
/// An append-only user-behavior signal (event sourcing). Feeds personalization and analytics.
/// </summary>
public class BehaviorEvent
{
    /// <summary>Gets or sets the surrogate primary key.</summary>
    public long Id { get; set; }

    /// <summary>Gets or sets the Jellyfin user id.</summary>
    public Guid UserId { get; set; }

    /// <summary>Gets or sets the Jellyfin item id this event concerns (if any).</summary>
    public Guid? JellyfinItemId { get; set; }

    /// <summary>Gets or sets the related catalog item id (if resolved).</summary>
    public long? CatalogItemId { get; set; }

    /// <summary>Gets or sets the event type.</summary>
    public BehaviorEventType EventType { get; set; }

    /// <summary>Gets or sets the signal value (e.g., completion fraction or rating).</summary>
    public double Value { get; set; }

    /// <summary>Gets or sets when the event occurred (UTC).</summary>
    public DateTime Timestamp { get; set; }

    /// <summary>Gets or sets extra context as JSON (device, session, hour-of-day, etc.).</summary>
    public string? ContextJson { get; set; }
}
