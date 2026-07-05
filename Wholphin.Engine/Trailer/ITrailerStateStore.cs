using System.Threading;
using System.Threading.Tasks;
using Wholphin.Engine.Data.Enums;

namespace Wholphin.Engine.Trailer;

/// <summary>
/// Reads and writes the per-title trailer state machine (the <c>TrailerAssets</c> table). All writes
/// upsert the single row for (tmdbId, mediaType); all methods are fail-soft so a database hiccup
/// degrades observability but never breaks trailer playback.
/// </summary>
public interface ITrailerStateStore
{
    /// <summary>Returns the current state for a title (<see cref="TrailerState.Unknown"/> if there's no row).</summary>
    /// <param name="tmdbId">The TMDB id.</param>
    /// <param name="mediaType">Movie or Series.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The current state.</returns>
    Task<TrailerState> GetStateAsync(int tmdbId, MediaType mediaType, CancellationToken ct = default);

    /// <summary>Transitions a title to <paramref name="state"/>, creating the row if needed.</summary>
    /// <param name="tmdbId">The TMDB id.</param>
    /// <param name="mediaType">Movie or Series.</param>
    /// <param name="state">The new state.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="failureReason">Optional failure reason (recorded + increments the failure count for the failed states).</param>
    /// <returns>A task.</returns>
    Task SetStateAsync(int tmdbId, MediaType mediaType, TrailerState state, CancellationToken ct = default, string? failureReason = null);

    /// <summary>Marks a title Ready with its cached file path/size, and optional preview metadata.</summary>
    /// <param name="tmdbId">The TMDB id.</param>
    /// <param name="mediaType">Movie or Series.</param>
    /// <param name="filePath">The cached transcoded file path.</param>
    /// <param name="fileBytes">The cached file size in bytes.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="durationMs">Optional probed duration (ms); only written when provided.</param>
    /// <param name="previewStartMs">Optional smart preview start offset (ms); only written when provided.</param>
    /// <returns>A task.</returns>
    Task MarkReadyAsync(int tmdbId, MediaType mediaType, string filePath, long fileBytes, CancellationToken ct = default, int? durationMs = null, int? previewStartMs = null);

    /// <summary>
    /// Records that a cached trailer was served — bumps AccessCount + LastAccessedAt (the LFU/recency
    /// inputs the cache manager evicts by). Throttled per title (in-memory) so the many range requests
    /// of a single playback don't hammer the DB, and safe to call fire-and-forget.
    /// </summary>
    /// <param name="tmdbId">The TMDB id.</param>
    /// <param name="mediaType">Movie or Series.</param>
    /// <returns>A task (completed immediately when throttled).</returns>
    Task RecordAccessAsync(int tmdbId, MediaType mediaType);
}
