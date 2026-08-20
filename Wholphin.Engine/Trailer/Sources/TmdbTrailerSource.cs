using System.Threading;
using System.Threading.Tasks;
using Wholphin.Engine.Integrations.Tmdb;
using Wholphin.Engine.Metadata;

namespace Wholphin.Engine.Trailer.Sources;

/// <summary>
/// TMDB's videos list — the primary trailer source, and the only one with structured type/official/
/// language flags to pick from.
/// </summary>
public class TmdbTrailerSource : ITrailerSource
{
    private readonly ITmdbClient _tmdb;

    /// <summary>Initializes a new instance of the <see cref="TmdbTrailerSource"/> class.</summary>
    /// <param name="tmdb">The TMDB port.</param>
    public TmdbTrailerSource(ITmdbClient tmdb) => _tmdb = tmdb;

    /// <inheritdoc />
    public string Name => "tmdb";

    /// <inheritdoc />
    public async Task<string?> ResolveAsync(MediaIdentity identity, string? preferredLanguage, CancellationToken cancellationToken)
    {
        if (!_tmdb.IsConfigured || identity.TmdbId is not > 0)
        {
            return null;
        }

        var picked = await _tmdb.GetTrailerUrlAsync(identity.TmdbId.Value, identity.MediaType, preferredLanguage, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(picked))
        {
            return picked;
        }

        // The full enrichment is TMDB-backed too, so this is usually moot — but it costs one call and
        // occasionally carries a URL the videos endpoint did not.
        var enrichment = await _tmdb.EnrichAsync(identity.TmdbId.Value, identity.MediaType, cancellationToken).ConfigureAwait(false);
        return enrichment?.TrailerUrl;
    }
}
