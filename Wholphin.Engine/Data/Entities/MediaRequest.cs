using System;
using Wholphin.Engine.Data.Enums;

namespace Wholphin.Engine.Data.Entities;

/// <summary>
/// A durable log of an engine-proxied (Jellyseerr) request — who requested what and when. Powers
/// the admin "recent requests" view and future request analytics. Added in schema v2 (the first
/// migration past the EnsureCreated baseline).
/// </summary>
public class MediaRequest
{
    /// <summary>Gets or sets the surrogate primary key.</summary>
    public long Id { get; set; }

    /// <summary>Gets or sets the Jellyfin user id that made the request.</summary>
    public Guid UserId { get; set; }

    /// <summary>Gets or sets the TMDB id of the requested title.</summary>
    public int TmdbId { get; set; }

    /// <summary>Gets or sets the media type.</summary>
    public MediaType MediaType { get; set; }

    /// <summary>Gets or sets the title at request time (best-effort).</summary>
    public string? Title { get; set; }

    /// <summary>Gets or sets the availability state recorded when the request was placed.</summary>
    public AvailabilityState Availability { get; set; }

    /// <summary>Gets or sets when the request was placed (UTC).</summary>
    public DateTime CreatedAt { get; set; }
}
