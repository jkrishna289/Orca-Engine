using System;

namespace Wholphin.Engine.Data.Entities;

/// <summary>
/// Per-user personalization state. Affinity vectors are stored as JSON and
/// interpreted by the personalization service.
/// </summary>
public class UserProfile
{
    /// <summary>Gets or sets the Jellyfin user id (primary key).</summary>
    public Guid UserId { get; set; }

    /// <summary>Gets or sets the serialized affinity vectors (genre/person/studio/etc.).</summary>
    public string? AffinityJson { get; set; }

    /// <summary>Gets or sets the serialized request-affinity (consumption-style) score.</summary>
    public string? RequestAffinityJson { get; set; }

    /// <summary>Gets or sets the number of behavior events folded into this profile (for confidence).</summary>
    public int EventCount { get; set; }

    /// <summary>Gets or sets when the profile was last updated.</summary>
    public DateTime UpdatedAt { get; set; }
}
