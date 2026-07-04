using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wholphin.Engine.Data.Enums;
using Wholphin.Engine.Integrations.Tmdb;

namespace Wholphin.Engine.Discovery.Sources;

/// <summary>
/// Global candidate source for the "What people are watching in {Country}" row. TMDB has no
/// per-country trending, so this uses the standard approximation: titles streamable in the
/// country (<c>watch_region</c> + flat-rate monetization) sorted by popularity, blended with a
/// local-original-language leg for regional flavor. Runs once per distinct configured country.
/// </summary>
public class CountrySource : IDiscoverySource
{
    private const int VoteFloor = 20;
    private const double RegionConfidence = 0.6;
    private const double LanguageConfidence = 0.5;

    private readonly ITmdbClient _tmdb;

    /// <summary>Initializes a new instance of the <see cref="CountrySource"/> class.</summary>
    /// <param name="tmdb">The TMDB client.</param>
    public CountrySource(ITmdbClient tmdb) => _tmdb = tmdb;

    /// <inheritdoc />
    public string Name => "country";

    /// <inheritdoc />
    public DiscoveryPickKind Kind => DiscoveryPickKind.Country;

    /// <inheritdoc />
    public DiscoverySourceScope Scope => DiscoverySourceScope.Global;

    /// <inheritdoc />
    public async Task<IReadOnlyList<DiscoveryCandidate>> GatherAsync(DiscoveryContext context, CancellationToken ct = default)
    {
        var candidates = new List<DiscoveryCandidate>();
        if (context.CountryCode is not { Length: 2 } cc)
        {
            return candidates;
        }

        var language = CountryLanguages.For(cc);
        foreach (var mediaType in new[] { MediaType.Movie, MediaType.Series })
        {
            // Leg 1: what's popular among titles streamable in the country.
            var regional = await _tmdb.DiscoverFilteredAsync(
                mediaType,
                new TmdbDiscoverFilters { WatchRegion = cc, VoteCountGte = VoteFloor },
                1,
                ct).ConfigureAwait(false);
            foreach (var result in regional)
            {
                candidates.Add(Candidate(result, RegionConfidence));
            }

            // Leg 2: local-language originals, for countries with a mapped dominant language.
            if (language is not null)
            {
                var local = await _tmdb.DiscoverFilteredAsync(
                    mediaType,
                    new TmdbDiscoverFilters { WithOriginalLanguage = language, VoteCountGte = VoteFloor },
                    1,
                    ct).ConfigureAwait(false);
                foreach (var result in local)
                {
                    candidates.Add(Candidate(result, LanguageConfidence));
                }
            }
        }

        return candidates;
    }

    private DiscoveryCandidate Candidate(Integrations.Jellyseerr.DiscoverResult result, double confidence) => new()
    {
        Result = result,
        Kind = DiscoveryPickKind.Country,
        Attributions = { new SourceAttribution { SourceType = Name, SourceConfidence = confidence } },
    };
}
