using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wholphin.Engine.Data.Enums;
using Wholphin.Engine.Integrations.Tmdb;
using Wholphin.Engine.Personalization;

namespace Wholphin.Engine.Discovery.Sources;

/// <summary>
/// Per-user candidate source for "You Might Like" picks: runs the real TMDB
/// <c>/discover</c> endpoint filtered to the user's top genres, sorted by popularity with a
/// vote-count floor so obscure titles don't slip in. Broad by design — the scoring stage does the
/// actual taste ranking; this source just narrows the pool to plausible territory.
/// </summary>
/// <remarks>
/// <para>
/// Two legs, because one was not enough. Genre alone, sorted by TMDB popularity, is a global
/// popularity contest that English-language cinema wins — so a viewer who watches mostly Hindi film
/// was offered Hollywood titles in her genres and almost nothing from the industry she actually
/// watches. The second leg re-runs the same genres constrained to her top languages.
/// </para>
/// <para>
/// The scoring stage still ranks everything that comes back; this only decides what is allowed to
/// be considered. A candidate that never enters the pool cannot be ranked into it later.
/// </para>
/// </remarks>
public class TasteDiscoverSource : IDiscoverySource
{
    /// <summary>Pages pulled per media type per run.</summary>
    public const int MaxPages = 2;

    /// <summary>Top languages given their own leg. Beyond two, the pull dilutes into noise.</summary>
    public const int MaxLanguages = 2;

    private const int MaxGenres = 3;
    private const int MovieVoteFloor = 200;
    private const int SeriesVoteFloor = 50;

    // Far lower than the global floors, deliberately. Within one language the pool is already small,
    // so a 200-vote bar calibrated against worldwide English releases removes most of a national
    // cinema rather than removing the obscure — which is precisely how the gap was created.
    private const int LanguageVoteFloor = 25;

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

            // Leg 1: the user's genres, however they rank globally.
            await GatherPagesAsync(
                candidates,
                mediaType,
                new TmdbDiscoverFilters
                {
                    WithGenres = ids,
                    VoteCountGte = mediaType == MediaType.Movie ? MovieVoteFloor : SeriesVoteFloor,
                },
                ct).ConfigureAwait(false);

            // Leg 2: the same genres, in the languages the user actually watches.
            foreach (var language in PullLanguages(profile))
            {
                await GatherPagesAsync(
                    candidates,
                    mediaType,
                    new TmdbDiscoverFilters
                    {
                        WithGenres = ids,
                        WithOriginalLanguage = language,
                        VoteCountGte = LanguageVoteFloor,
                    },
                    ct).ConfigureAwait(false);
            }
        }

        return candidates;
    }

    /// <summary>
    /// The languages worth their own pull: the profile's strongest, minus English.
    /// </summary>
    /// <param name="profile">The user's taste profile.</param>
    /// <returns>The ISO-639-1 codes to run a language leg for.</returns>
    /// <remarks>
    /// English is skipped because leg 1 already is an English leg in all but name — TMDB popularity
    /// ordering surfaces English-language titles regardless. Spending a second round trip to ask for
    /// them again would buy nothing.
    /// </remarks>
    public static IReadOnlyList<string> PullLanguages(UserTasteProfile profile) => profile.TopLanguages
        .Where(code => !string.IsNullOrWhiteSpace(code))
        .Select(code => code.Trim())
        .Where(code => !string.Equals(code, "en", StringComparison.OrdinalIgnoreCase))
        .Take(MaxLanguages)
        .ToList();

    private async Task GatherPagesAsync(
        List<DiscoveryCandidate> candidates,
        MediaType mediaType,
        TmdbDiscoverFilters filters,
        CancellationToken ct)
    {
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
}
