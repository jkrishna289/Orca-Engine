using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wholphin.Engine.Data.Enums;
using Wholphin.Engine.Integrations.Tmdb;

namespace Wholphin.Engine.Discovery.Sources;

/// <summary>
/// Per-user candidate source for "Because You Watched" picks: pulls TMDB's related titles
/// (recommendations + content-similar) for the user's strongest recent seed titles. Each candidate
/// carries its seed so the justification ("Because you watched X") survives to the pick.
/// </summary>
public class SeedRelatedSource : IDiscoverySource
{
    /// <summary>How many taste seeds drive related pulls per run (2 TMDB calls each).</summary>
    public const int MaxSeeds = 5;

    private const double RecommendationsConfidence = 0.8;
    private const double SimilarConfidence = 0.7;

    private readonly ITmdbClient _tmdb;

    /// <summary>Initializes a new instance of the <see cref="SeedRelatedSource"/> class.</summary>
    /// <param name="tmdb">The TMDB client.</param>
    public SeedRelatedSource(ITmdbClient tmdb) => _tmdb = tmdb;

    /// <inheritdoc />
    public string Name => "seedrelated";

    /// <inheritdoc />
    public DiscoveryPickKind Kind => DiscoveryPickKind.BecauseYouWatched;

    /// <inheritdoc />
    public DiscoverySourceScope Scope => DiscoverySourceScope.PerUser;

    /// <inheritdoc />
    public async Task<IReadOnlyList<DiscoveryCandidate>> GatherAsync(DiscoveryContext context, CancellationToken ct = default)
    {
        var candidates = new List<DiscoveryCandidate>();
        if (context.Profile is not { } profile)
        {
            return candidates;
        }

        foreach (var seed in profile.PullSeeds(MaxSeeds))
        {
            foreach (var kind in new[] { TmdbRelatedKind.Recommendations, TmdbRelatedKind.Similar })
            {
                var related = await _tmdb.GetRelatedAsync(seed.TmdbId!.Value, seed.MediaType, kind, ct).ConfigureAwait(false);
                var confidence = kind == TmdbRelatedKind.Recommendations ? RecommendationsConfidence : SimilarConfidence;
                foreach (var result in related)
                {
                    candidates.Add(new DiscoveryCandidate
                    {
                        Result = result,
                        Kind = DiscoveryPickKind.BecauseYouWatched,
                        Seed = seed,
                        Attributions = { new SourceAttribution { SourceType = Name, SourceConfidence = confidence } },
                    });
                }
            }
        }

        return candidates;
    }
}
