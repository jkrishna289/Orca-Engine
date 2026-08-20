using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wholphin.Engine.Http;
using Wholphin.Engine.Integrations.Tmdb;

namespace Wholphin.Engine.Metadata.Providers;

/// <summary>
/// TMDB as an <see cref="IMetadataProvider"/>. Writes no HTTP of its own — it adapts the existing
/// <see cref="ITmdbClient"/>, which already owns TMDB auth, 429 retry, its own counters and its
/// fail-soft contract.
/// </summary>
/// <remarks>
/// Because <see cref="ITmdbClient"/> degrades to null rather than throwing, TMDB's circuit breaker
/// never trips — deliberately. TMDB is the primary provider and disabling it would do more harm than
/// the timeouts a breaker saves; its failures are already visible as the <c>tmdb.*.error</c> counters
/// the Observatory has always shown. The gate is still used, for uniform latency and call accounting.
/// </remarks>
public class TmdbMetadataProvider : IMetadataProvider
{
    /// <summary>TMDB serves posters at a fixed w500 and backdrops at w1280; 2:3 and 16:9 respectively.</summary>
    private const int PosterWidth = 500;
    private const int PosterHeight = 750;
    private const int BackdropWidth = 1280;
    private const int BackdropHeight = 720;

    private readonly ITmdbClient _tmdb;
    private readonly IProviderGate _gate;

    /// <summary>
    /// Initializes a new instance of the <see cref="TmdbMetadataProvider"/> class.
    /// </summary>
    /// <param name="tmdb">The existing TMDB port.</param>
    /// <param name="gate">The shared provider gate.</param>
    public TmdbMetadataProvider(ITmdbClient tmdb, IProviderGate gate)
    {
        _tmdb = tmdb;
        _gate = gate;
    }

    /// <inheritdoc />
    public string Name => "tmdb";

    /// <inheritdoc />
    public MetadataCapability Capabilities =>
        MetadataCapability.Core | MetadataCapability.Artwork | MetadataCapability.Ratings |
        MetadataCapability.Ids | MetadataCapability.Trailer;

    /// <inheritdoc />
    public bool IsConfigured => _tmdb.IsConfigured;

    /// <inheritdoc />
    public Task<MetadataFragment?> FetchAsync(MediaIdentity identity, MetadataCapability wanted, CancellationToken cancellationToken)
    {
        if (identity.TmdbId is not > 0)
        {
            return Task.FromResult<MetadataFragment?>(null);
        }

        return _gate.ExecuteAsync(Name, async ct =>
        {
            var enrichment = await _tmdb.EnrichAsync(identity.TmdbId.Value, identity.MediaType, ct).ConfigureAwait(false);
            return enrichment is null ? null : Project(enrichment);
        }, cancellationToken);
    }

    /// <summary>Maps a TMDB enrichment onto the shared fragment shape.</summary>
    /// <param name="e">The TMDB enrichment.</param>
    /// <returns>The fragment.</returns>
    internal static MetadataFragment Project(TmdbEnrichment e) => new()
    {
        Source = "tmdb",
        ImdbId = e.ImdbId,
        TvdbId = e.TvdbId,
        Overview = e.Overview,
        Year = e.Year,
        RuntimeMinutes = e.RuntimeMinutes,
        OriginalLanguage = e.OriginalLanguage,
        CollectionName = e.CollectionName,
        Genres = e.Genres,
        Keywords = e.Keywords,
        People = e.People,
        Poster = Image(e.PosterImageUrl, PosterWidth, PosterHeight),
        Backdrop = Image(e.BackdropImageUrl, BackdropWidth, BackdropHeight),
        CommunityRating = e.CommunityRating,
        TrailerUrl = e.TrailerUrl,
        Ratings = e.CommunityRating is { } rating
            ? new Dictionary<string, double>(StringComparer.Ordinal) { ["tmdb"] = Math.Round(rating * 10, 1) }
            : new Dictionary<string, double>(StringComparer.Ordinal),
    };

    private static ImageCandidate? Image(string? url, int width, int height)
        => string.IsNullOrWhiteSpace(url) ? null : new ImageCandidate(url, width, height, null, 0);
}
