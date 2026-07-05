using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wholphin.Engine.Data.Enums;

namespace Wholphin.Engine.Trailer;

/// <summary>
/// Predicts which titles' trailers are most worth warming next (Phase 7 of the trailer redesign),
/// so the cache favours what users are actually likely to watch rather than just the highest-rated
/// titles. Blends every signal the engine already captures — Continue Watching, recent focus/click/
/// trailer engagement (recency- and time-of-day-weighted), longitudinal per-item interest, and
/// popularity — into a single ranked candidate list, with a rating-based fallback for a cold start.
/// </summary>
public interface ITrailerPredictionService
{
    /// <summary>Returns up to <paramref name="max"/> (tmdbId, mediaType) pairs to warm, most-likely first.</summary>
    /// <param name="max">Maximum candidates to return.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The ranked warm candidates.</returns>
    Task<IReadOnlyList<(int TmdbId, MediaType MediaType)>> GetWarmCandidatesAsync(int max, CancellationToken ct = default);
}
