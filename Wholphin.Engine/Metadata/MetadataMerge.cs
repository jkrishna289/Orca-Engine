using System;
using System.Collections.Generic;
using System.Linq;

namespace Wholphin.Engine.Metadata;

/// <summary>
/// Collapses several providers' partial fragments into one best-of-breed record, field by field.
/// </summary>
/// <remarks>
/// Pure and deterministic on purpose — this is the whole unit-test surface of the aggregation layer,
/// and merge bugs are otherwise invisible until a poster silently regresses in production.
/// </remarks>
public static class MetadataMerge
{
    // Image scoring. Language leads because a poster with the wrong language's text burned in is
    // WRONG, not merely lower quality; resolution then decides between correct ones. The admin's
    // priority is the smallest term: it breaks ties and settles otherwise-equal candidates, but it
    // cannot make a 500px poster beat a 1000px one — that is the point of scoring rather than
    // ordering.
    private const int LanguageExactScore = 25;
    private const int LanguageNeutralScore = 10;
    private const int MaxScoredWidth = 2000;
    private const int MaxScoredVotes = 100;
    private const int RankStepScore = 2;
    private const int RankSteps = 10;

    /// <summary>
    /// Merges fragments into one, resolving each field group by its own configured provider order.
    /// </summary>
    /// <param name="fragments">The per-provider fragments; nulls are ignored.</param>
    /// <param name="priority">The per-field provider order.</param>
    /// <param name="language">The preferred artwork language.</param>
    /// <returns>The merged fragment. Its <see cref="MetadataFragment.Source"/> is empty — provenance is per field, not per record.</returns>
    public static MetadataFragment Merge(
        IReadOnlyList<MetadataFragment?> fragments,
        MetadataPriority priority,
        string language)
    {
        var present = fragments.Where(f => f is not null).Select(f => f!).ToList();
        if (present.Count == 0)
        {
            return new MetadataFragment();
        }

        var core = ByRank(present, priority, MetadataCapability.Core);
        var ids = ByRank(present, priority, MetadataCapability.Core);

        return new MetadataFragment
        {
            // Scalars and lists: first non-empty in priority order. Deliberately no scoring — a
            // longer overview is not a better overview, it is just a different provider's voice.
            Overview = First(core, f => f.Overview),
            Year = First(core, f => f.Year),
            RuntimeMinutes = First(core, f => f.RuntimeMinutes),
            OriginalLanguage = First(core, f => f.OriginalLanguage),
            CollectionName = First(core, f => f.CollectionName),
            Genres = FirstList(core, f => f.Genres),
            Keywords = FirstList(core, f => f.Keywords),
            People = FirstList(core, f => f.People),
            ImdbId = First(ids, f => f.ImdbId),
            TvdbId = First(ids, f => f.TvdbId),

            Poster = BestImage(present, priority, MetadataCapability.Artwork, language, f => f.Poster),
            Backdrop = BestImage(present, priority, MetadataCapability.Artwork, language, f => f.Backdrop),
            Logo = BestImage(present, priority, MetadataCapability.Logo, language, f => f.Logo),

            CommunityRating = First(ByRank(present, priority, MetadataCapability.Ratings), f => f.CommunityRating),
            Ratings = MergeRatings(ByRank(present, priority, MetadataCapability.Ratings)),

            TrailerUrl = First(ByRank(present, priority, MetadataCapability.Core), f => f.TrailerUrl),
        };
    }

    /// <summary>
    /// Scores one artwork candidate. Exposed for tests: this is where a silent poster regression
    /// would hide.
    /// </summary>
    /// <param name="image">The candidate.</param>
    /// <param name="language">The preferred language.</param>
    /// <param name="rank">The provider's rank for this field group (0 is best).</param>
    /// <returns>The score; higher wins.</returns>
    public static int ScoreImage(ImageCandidate image, string language, int rank)
    {
        var languageScore = string.Equals(image.Language, language, StringComparison.OrdinalIgnoreCase)
            ? LanguageExactScore
            : string.IsNullOrWhiteSpace(image.Language) || image.Language == "00"
                ? LanguageNeutralScore
                : 0;

        var widthScore = Math.Clamp(image.Width, 0, MaxScoredWidth) / 100;
        var voteScore = Math.Clamp(image.Votes, 0, MaxScoredVotes) / 10;
        var rankScore = Math.Max(0, RankSteps - rank) * RankStepScore;

        return languageScore + widthScore + voteScore + rankScore;
    }

    private static IReadOnlyList<MetadataFragment> ByRank(
        IReadOnlyList<MetadataFragment> fragments,
        MetadataPriority priority,
        MetadataCapability capability)
        => fragments.OrderBy(f => priority.Rank(capability, f.Source)).ToList();

    private static ImageCandidate? BestImage(
        IReadOnlyList<MetadataFragment> fragments,
        MetadataPriority priority,
        MetadataCapability capability,
        string language,
        Func<MetadataFragment, ImageCandidate?> select)
    {
        ImageCandidate? best = null;
        var bestScore = int.MinValue;

        foreach (var fragment in fragments)
        {
            if (select(fragment) is not { } candidate || string.IsNullOrWhiteSpace(candidate.Url))
            {
                continue;
            }

            var score = ScoreImage(candidate, language, priority.Rank(capability, fragment.Source));
            if (score > bestScore)
            {
                best = candidate;
                bestScore = score;
            }
        }

        return best;
    }

    /// <summary>
    /// Unions ratings PER KEY rather than per fragment, so OMDb's Rotten Tomatoes score and TMDB's
    /// community score coexist instead of one record winning wholesale.
    /// </summary>
    private static IReadOnlyDictionary<string, double> MergeRatings(IReadOnlyList<MetadataFragment> ranked)
    {
        var merged = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var fragment in ranked)
        {
            foreach (var (key, value) in fragment.Ratings)
            {
                // First writer wins: `ranked` is already in priority order.
                merged.TryAdd(key, value);
            }
        }

        return merged;
    }

    private static T? First<T>(IReadOnlyList<MetadataFragment> ranked, Func<MetadataFragment, T?> select)
        where T : struct
    {
        foreach (var fragment in ranked)
        {
            if (select(fragment) is { } value)
            {
                return value;
            }
        }

        return null;
    }

    private static string? First(IReadOnlyList<MetadataFragment> ranked, Func<MetadataFragment, string?> select)
        => ranked.Select(select).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static IReadOnlyList<string> FirstList(IReadOnlyList<MetadataFragment> ranked, Func<MetadataFragment, IReadOnlyList<string>> select)
        => ranked.Select(select).FirstOrDefault(v => v.Count > 0) ?? Array.Empty<string>();
}
