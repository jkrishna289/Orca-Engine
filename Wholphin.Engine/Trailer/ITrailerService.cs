using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wholphin.Engine.Data.Enums;

namespace Wholphin.Engine.Trailer;

/// <summary>
/// Server-side trailer pre-buffer/transcode (Milestone 8, Phase 3). Resolves a title's YouTube trailer
/// (from the catalog/TMDB), downloads it with yt-dlp, transcodes a small low-bitrate clip with ffmpeg,
/// and caches it on disk so the app can stream a directly-playable trailer on card focus. Entirely
/// fail-soft: if yt-dlp/ffmpeg aren't installed it reports unavailable and does nothing.
/// </summary>
public interface ITrailerService
{
    /// <summary>Gets a value indicating whether yt-dlp and ffmpeg are both available on the host.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Returns the cached trailer file path for a title, transcoding it on demand if missing (bounded;
    /// dedupes concurrent requests). Null when unavailable / no trailer / on failure.
    /// </summary>
    /// <param name="tmdbId">The TMDB id.</param>
    /// <param name="mediaType">Movie or Series.</param>
    /// <param name="allowDownload">When false, only returns an already-cached file (used by the streaming endpoint).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The local file path, or null.</returns>
    Task<string?> GetTrailerPathAsync(int tmdbId, MediaType mediaType, bool allowDownload, CancellationToken ct = default);

    /// <summary>Warms the cache for a set of titles (Trending / Recommended / Continue Watching only).</summary>
    /// <param name="items">The (tmdbId, mediaType) pairs to pre-buffer.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of trailers cached.</returns>
    Task<int> PrebufferAsync(IEnumerable<(int TmdbId, MediaType MediaType)> items, CancellationToken ct = default);
}
