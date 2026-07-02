using System.Text.Json.Serialization;

namespace Wholphin.Engine.Data.Enums;

/// <summary>
/// Availability of a catalog item in the unified (available + requestable) catalog.
/// Availability is a ranking signal, not a sort key (see the recommendation engine).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AvailabilityState
{
    /// <summary>▶️ Playable now (present in the Jellyfin library).</summary>
    WatchNow = 0,

    /// <summary>📥 Obtainable via Jellyseerr (not yet in the library).</summary>
    Request = 1,

    /// <summary>⏳ A request has been placed.</summary>
    Requested = 2,

    /// <summary>🔄 Being acquired/downloaded.</summary>
    Downloading = 3,

    /// <summary>✅ A request was just fulfilled and added.</summary>
    RecentlyAdded = 4,

    /// <summary>🚫 Cannot be obtained (penalized/hidden).</summary>
    Unavailable = 5
}
