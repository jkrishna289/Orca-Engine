using Wholphin.Engine.Data.Enums;

namespace Wholphin.Engine.Trailer;

/// <summary>
/// The single admission point for all trailer production. Every producer — a focused card, the hero
/// billboard, the background pre-buffer worker, the scheduled warmer — enqueues here with a
/// <see cref="TrailerPriority"/> instead of spawning its own download. A bounded worker pool drains
/// the queue highest-priority-first, so user-visible content is always served first and the host can
/// never be swamped by unbounded concurrent yt-dlp/ffmpeg processes.
/// </summary>
public interface ITrailerQueue
{
    /// <summary>
    /// Requests that a title's trailer be produced (downloaded + transcoded) at the given priority.
    /// Idempotent and non-blocking: duplicates are coalesced (keeping the best priority), already-cached
    /// or in-flight titles are ignored, and it is a no-op when yt-dlp/ffmpeg are unavailable.
    /// </summary>
    /// <param name="tmdbId">The TMDB id.</param>
    /// <param name="mediaType">Movie or Series (other types are ignored).</param>
    /// <param name="priority">How urgently to produce it.</param>
    /// <param name="lang">Preferred trailer audio language (ISO 639-1), or null for the configured default.</param>
    void Enqueue(int tmdbId, MediaType mediaType, TrailerPriority priority, string? lang = null);

    /// <summary>Gets a value indicating whether a title is currently waiting in the queue (not yet in-flight).</summary>
    /// <param name="tmdbId">The TMDB id.</param>
    /// <param name="mediaType">Movie or Series.</param>
    /// <returns>True if the title is queued and waiting.</returns>
    bool IsQueued(int tmdbId, MediaType mediaType);

    /// <summary>Gets the number of titles currently waiting in the queue (for diagnostics).</summary>
    int Depth { get; }
}
