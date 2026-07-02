using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wholphin.Engine.Data.Enums;

namespace Wholphin.Engine.Integrations.Jellyseerr;

/// <summary>
/// Port over Jellyseerr's REST API (requests + availability + discovery). The adapter is
/// fail-soft: any transport/parse error returns a neutral result so the engine never breaks
/// when Jellyseerr is unconfigured or unreachable.
/// </summary>
public interface IJellyseerrClient
{
    /// <summary>Gets a value indicating whether a Jellyseerr URL and API key are configured.</summary>
    bool IsConfigured { get; }

    /// <summary>Resolves the current availability of a title, or null when unknown/unreachable.</summary>
    /// <param name="tmdbId">The TMDB id.</param>
    /// <param name="mediaType">Movie or Series.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The mapped availability, or null.</returns>
    Task<AvailabilityState?> GetAvailabilityAsync(int tmdbId, MediaType mediaType, CancellationToken ct = default);

    /// <summary>
    /// Submits a request for a title, attributed to the requesting user when that user can be mapped
    /// to a Jellyseerr account (falls back to the API-key owner otherwise). Returns false on failure.
    /// </summary>
    /// <param name="tmdbId">The TMDB id.</param>
    /// <param name="mediaType">Movie or Series.</param>
    /// <param name="jellyfinUserId">The requesting Jellyfin user (mapped to a Jellyseerr user for attribution).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True when Jellyseerr accepted the request.</returns>
    Task<bool> RequestAsync(int tmdbId, MediaType mediaType, System.Guid jellyfinUserId, CancellationToken ct = default);

    /// <summary>Fetches a page of discovery results (popular/trending), or empty on failure.</summary>
    /// <param name="mediaType">Movie or Series.</param>
    /// <param name="page">1-based page number.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The normalized results (empty when unavailable).</returns>
    Task<IReadOnlyList<DiscoverResult>> DiscoverAsync(MediaType mediaType, int page, CancellationToken ct = default);
}
