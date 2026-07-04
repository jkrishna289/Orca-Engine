using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wholphin.Engine.Data.Enums;
using Wholphin.Engine.Integrations.Tmdb;

namespace Wholphin.Engine.Discovery.Sources;

/// <summary>
/// Global candidate source for the "Trending" row: TMDB's weekly trending lists (movie + TV).
/// Honest global popularity, not a taste claim — TMDB trending has no per-region variant, so this
/// is world-wide by nature. Personal flavor is added later at render time (the trending row blends
/// in the viewer's taste cosine).
/// </summary>
public class TrendingSource : IDiscoverySource
{
    private const double Confidence = 0.9;

    private readonly ITmdbClient _tmdb;

    /// <summary>Initializes a new instance of the <see cref="TrendingSource"/> class.</summary>
    /// <param name="tmdb">The TMDB client.</param>
    public TrendingSource(ITmdbClient tmdb) => _tmdb = tmdb;

    /// <inheritdoc />
    public string Name => "trending";

    /// <inheritdoc />
    public DiscoveryPickKind Kind => DiscoveryPickKind.Trending;

    /// <inheritdoc />
    public DiscoverySourceScope Scope => DiscoverySourceScope.Global;

    /// <inheritdoc />
    public async Task<IReadOnlyList<DiscoveryCandidate>> GatherAsync(DiscoveryContext context, CancellationToken ct = default)
    {
        var candidates = new List<DiscoveryCandidate>();
        foreach (var mediaType in new[] { MediaType.Movie, MediaType.Series })
        {
            var results = await _tmdb.DiscoverAsync(mediaType, TmdbDiscoverCategory.Trending, 1, ct).ConfigureAwait(false);
            foreach (var result in results)
            {
                candidates.Add(new DiscoveryCandidate
                {
                    Result = result,
                    Kind = DiscoveryPickKind.Trending,
                    Attributions = { new SourceAttribution { SourceType = Name, SourceConfidence = Confidence } },
                });
            }
        }

        return candidates;
    }
}
