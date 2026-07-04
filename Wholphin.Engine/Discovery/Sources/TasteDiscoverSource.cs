using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wholphin.Engine.Data.Enums;
using Wholphin.Engine.Integrations.Tmdb;

namespace Wholphin.Engine.Discovery.Sources;

/// <summary>
/// Per-user candidate source for "You Might Like" picks: runs the real TMDB
/// <c>/discover</c> endpoint filtered to the user's top genres, sorted by popularity with a
/// vote-count floor so obscure titles don't slip in. Broad by design — the scoring stage does the
/// actual taste ranking; this source just narrows the pool to plausible territory.
/// </summary>
public class TasteDiscoverSource : IDiscoverySource
{
    /// <summary>Pages pulled per media type per run.</summary>
    public const int MaxPages = 2;

    private const int MaxGenres = 3;
    private const int MovieVoteFloor = 200;
    private const int SeriesVoteFloor = 50;
    private const double Confidence = 0.6;

    private readonly ITmdbClient _tmdb;

    /// <summary>Initializes a new instance of the <see cref="TasteDiscoverSource"/> class.</summary>
    /// <param name="tmdb">The TMDB client.</param>
    public TasteDiscoverSource(ITmdbClient tmdb) => _tmdb = tmdb;

    /// <inheritdoc />
    public string Name => "tastediscover";

    /// <inheritdoc />
    public DiscoveryPickKind Kind => DiscoveryPickKind.TasteMatch;

    /// <inheritdoc />
    public DiscoverySourceScope Scope => DiscoverySourceScope.PerUser;

    /// <inheritdoc />
    public async Task<IReadOnlyList<DiscoveryCandidate>> GatherAsync(DiscoveryContext context, CancellationToken ct = default)
    {
        var candidates = new List<DiscoveryCandidate>();
        if (context.Profile is not { TopGenres.Count: > 0 } profile)
        {
            return candidates;
        }

        foreach (var mediaType in new[] { MediaType.Movie, MediaType.Series })
        {
            if (!context.GenreIdsByName.TryGetValue(mediaType, out var genreIds))
            {
                continue;
            }

            var ids = profile.TopGenres
                .Select(name => genreIds.TryGetValue(name, out var id) ? id : 0)
                .Where(id => id > 0)
                .Take(MaxGenres)
                .ToList();
            if (ids.Count == 0)
            {
                continue;
            }

            var filters = new TmdbDiscoverFilters
            {
                WithGenres = ids,
                VoteCountGte = mediaType == MediaType.Movie ? MovieVoteFloor : SeriesVoteFloor,
            };

            for (var page = 1; page <= MaxPages; page++)
            {
                var results = await _tmdb.DiscoverFilteredAsync(mediaType, filters, page, ct).ConfigureAwait(false);
                if (results.Count == 0)
                {
                    break;
                }

                foreach (var result in results)
                {
                    candidates.Add(new DiscoveryCandidate
                    {
                        Result = result,
                        Kind = DiscoveryPickKind.TasteMatch,
                        Attributions = { new SourceAttribution { SourceType = Name, SourceConfidence = Confidence } },
                    });
                }
            }
        }

        return candidates;
    }
}
