using System.Threading;
using System.Threading.Tasks;
using Wholphin.Engine.Metadata;

namespace Wholphin.Engine.Trailer.Sources;

/// <summary>
/// TheTVDB's trailer list, reached through the metadata aggregator so it inherits the circuit
/// breaker, throttle and provider cache rather than re-implementing them.
/// </summary>
/// <remarks>
/// Useful mainly for series, where TVDB's coverage is strongest and TMDB's video list is thinnest.
/// </remarks>
public class TvdbTrailerSource : ITrailerSource
{
    private readonly IMetadataAggregator _aggregator;

    /// <summary>Initializes a new instance of the <see cref="TvdbTrailerSource"/> class.</summary>
    /// <param name="aggregator">The metadata aggregator.</param>
    public TvdbTrailerSource(IMetadataAggregator aggregator) => _aggregator = aggregator;

    /// <inheritdoc />
    public string Name => "tvdb";

    /// <inheritdoc />
    public async Task<string?> ResolveAsync(MediaIdentity identity, string? preferredLanguage, CancellationToken cancellationToken)
    {
        if (identity.TvdbId is not > 0)
        {
            return null;
        }

        var fragment = await _aggregator
            .FetchAsync(identity, MetadataCapability.Trailer, cancellationToken)
            .ConfigureAwait(false);

        return fragment?.TrailerUrl;
    }
}
