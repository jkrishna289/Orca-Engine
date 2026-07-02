using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wholphin.Engine.Data.Enums;
using Wholphin.Engine.Integrations.Jellyseerr;

namespace Wholphin.Engine.Integrations.Tmdb;

/// <summary>
/// Fail-soft port over the TMDB v3 REST API. Provides genre-name resolution, per-title metadata
/// enrichment (genres + artwork + trailer), and direct trending/popular discovery — the second
/// hybrid discovery source alongside Jellyseerr. Every method degrades to a neutral result
/// (null / empty) when TMDB is unconfigured or a call fails.
/// </summary>
public interface ITmdbClient
{
    /// <summary>Gets a value indicating whether a TMDB API key is configured.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Resolves the TMDB genre-id → name map for a media type (cached). Used to turn the genre ids
    /// on discover results into the genre names the catalog/affinity layers consume.
    /// </summary>
    /// <param name="mediaType">Movie or Series.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The id→name map (empty when unconfigured or on failure).</returns>
    Task<IReadOnlyDictionary<int, string>> GetGenreMapAsync(MediaType mediaType, CancellationToken ct = default);

    /// <summary>
    /// Fetches a TMDB title's detail and projects the fields the catalog cares about (genre names,
    /// poster/backdrop URLs, trailer URL, overview, year, rating, runtime).
    /// </summary>
    /// <param name="tmdbId">The TMDB id.</param>
    /// <param name="mediaType">Movie or Series.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The enrichment, or null when unconfigured/not found/on failure.</returns>
    Task<TmdbEnrichment?> EnrichAsync(int tmdbId, MediaType mediaType, CancellationToken ct = default);

    /// <summary>
    /// Pulls a page of TMDB-direct discovery results (trending/popular/top-rated), already mapped to
    /// the engine's normalized <see cref="DiscoverResult"/> with genre names + artwork resolved.
    /// </summary>
    /// <param name="mediaType">Movie or Series.</param>
    /// <param name="category">Which TMDB list to pull from.</param>
    /// <param name="page">The 1-based page number.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The results (empty when unconfigured or on failure).</returns>
    Task<IReadOnlyList<DiscoverResult>> DiscoverAsync(MediaType mediaType, TmdbDiscoverCategory category, int page, CancellationToken ct = default);

    /// <summary>Fetches a title's keyword/theme names (TMDB keywords).</summary>
    /// <param name="tmdbId">The TMDB id.</param>
    /// <param name="mediaType">Movie or Series.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The keyword names (empty when unconfigured/not found/on failure).</returns>
    Task<IReadOnlyList<string>> GetKeywordsAsync(int tmdbId, MediaType mediaType, CancellationToken ct = default);

    /// <summary>
    /// Pulls TMDB's related titles for a source (recommendations or content-similar), mapped to the
    /// normalized <see cref="DiscoverResult"/>.
    /// </summary>
    /// <param name="tmdbId">The source TMDB id.</param>
    /// <param name="mediaType">Movie or Series.</param>
    /// <param name="kind">Recommendations or Similar.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The related titles (empty when unconfigured or on failure).</returns>
    Task<IReadOnlyList<DiscoverResult>> GetRelatedAsync(int tmdbId, MediaType mediaType, TmdbRelatedKind kind, CancellationToken ct = default);

    /// <summary>Fetches a series' next episode to air (the "Coming Soon" signal). Series only.</summary>
    /// <param name="tmdbId">The series TMDB id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The upcoming episode, or null when none/unconfigured/on failure.</returns>
    Task<TmdbUpcomingEpisode?> GetNextEpisodeAsync(int tmdbId, CancellationToken ct = default);

    /// <summary>
    /// Resolves a title's primary streaming-provider brand (the "on Netflix/Prime/Disney+" studio tag) for
    /// a region, from TMDB watch providers — the flat-rate provider with the lowest display priority.
    /// </summary>
    /// <param name="tmdbId">The TMDB id.</param>
    /// <param name="mediaType">Movie or Series.</param>
    /// <param name="region">ISO-3166-1 country code (e.g., "US", "IN").</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The primary provider brand, or null when none/unconfigured/on failure.</returns>
    Task<TmdbProviderBrand?> GetPrimaryProviderAsync(int tmdbId, MediaType mediaType, string region, CancellationToken ct = default);
}
