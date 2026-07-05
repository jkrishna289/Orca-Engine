using System.Threading;
using System.Threading.Tasks;
using Wholphin.Engine.Data.Enums;

namespace Wholphin.Engine.Trailer;

/// <summary>
/// Server-side trailer pre-buffer/transcode (Milestone 8, Phase 3; re-architected in the trailer
/// redesign). Resolves a title's YouTube trailer (from the catalog/TMDB), downloads it with yt-dlp,
/// transcodes a low-bitrate preview with ffmpeg, and caches it on disk. Production is now driven
/// exclusively by <see cref="ITrailerQueue"/> (which bounds concurrency); this service owns the
/// cache-file lookup and the state-transitioning execution. Entirely fail-soft: if yt-dlp/ffmpeg
/// aren't installed it reports unavailable and does nothing.
/// </summary>
public interface ITrailerService
{
    /// <summary>Gets a value indicating whether yt-dlp and ffmpeg are both available on the host.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Returns the cached trailer file path for a title if a non-empty transcoded file already exists,
    /// else null. Pure, fast, synchronous disk check — never downloads (that goes through the queue).
    /// </summary>
    /// <param name="tmdbId">The TMDB id.</param>
    /// <param name="mediaType">Movie or Series.</param>
    /// <returns>The local file path, or null.</returns>
    string? GetCachedPath(int tmdbId, MediaType mediaType);

    /// <summary>
    /// Runs the full produce pipeline for one title — resolve URL → download → transcode → cache —
    /// transitioning the trailer state machine at each step. Called only by the queue's worker pool,
    /// which guarantees single-flight per title and bounded concurrency. Returns the terminal state.
    /// </summary>
    /// <param name="tmdbId">The TMDB id.</param>
    /// <param name="mediaType">Movie or Series.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The terminal <see cref="TrailerState"/> (Ready / FailedTemporary / FailedPermanent).</returns>
    Task<TrailerState> ProcessAsync(int tmdbId, MediaType mediaType, CancellationToken ct = default);
}
