using System.Text.Json.Serialization;

namespace Wholphin.Engine.Data.Enums;

/// <summary>
/// Explicit lifecycle of a server-side trailer asset (the trailer redesign's core primitive).
/// Replaces the old "the file either exists or you get a 404" implicit protocol: every trailer now
/// has an addressable state so producers (queue, workers) and consumers (the client, once it adopts
/// the status endpoint in a later milestone) can reason about "downloading, wait" vs. "permanently
/// unavailable, give up" instead of inferring intent from repeated HTTP 404s.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TrailerState
{
    /// <summary>Nothing is known about this title's trailer yet (no row, not queued, not cached).</summary>
    Unknown = 0,

    /// <summary>Known to have no obtainable trailer (reserved; today folded into <see cref="FailedPermanent"/>).</summary>
    NotAvailable = 1,

    /// <summary>Resolving the source trailer URL (catalog row, then TMDB).</summary>
    Discovering = 2,

    /// <summary>Accepted into the priority queue, waiting for a worker slot.</summary>
    Queued = 3,

    /// <summary>yt-dlp is fetching the source stream.</summary>
    Downloading = 4,

    /// <summary>ffmpeg is transcoding the cached preview.</summary>
    Transcoding = 5,

    /// <summary>A playable transcoded file exists on disk.</summary>
    Ready = 6,

    /// <summary>Client-reported "currently playing" (reserved for the client protocol milestone).</summary>
    Playing = 7,

    /// <summary>Failed for a transient reason (network, timeout, server busy) — safe to retry with backoff.</summary>
    FailedTemporary = 8,

    /// <summary>Failed permanently (no trailer exists, source removed, geo-blocked) — do not retry.</summary>
    FailedPermanent = 9,

    /// <summary>Was ready but the cached file has been evicted/expired (reserved for the cache-manager milestone).</summary>
    Expired = 10,
}
