using System;
using System.Linq;
using Wholphin.Engine.Data.Entities;
using Wholphin.Engine.Data.Enums;
using Wholphin.Engine.Recommendation;
using Xunit;

namespace Wholphin.Engine.Tests;

/// <summary>
/// More Like This: TMDB's related titles merged with the local ranking. The failures are quiet — the
/// same show twice, the source recommending itself, or genre matches drowning out real ones.
/// </summary>
public class MoreLikeThisMergeTests
{
    private static CatalogItem Item(long id, int? tmdb, string title, bool library = false) => new()
    {
        Id = id,
        TmdbId = tmdb,
        MediaType = MediaType.Series,
        Title = title,
        JellyfinItemId = library ? Guid.NewGuid() : null,
        Availability = library ? AvailabilityState.WatchNow : AvailabilityState.Request,
    };

    private static SimilarityResult R(CatalogItem item, double score) => new(item, score);

    [Fact]
    public void KeepsOneEntryPerTitleAndPrefersTheLibraryCopy()
    {
        var source = Item(1, 116727, "ODDTAXI");
        var tmdbCopy = Item(2, 100911, "Asur");
        var libraryCopy = Item(3, 100911, "Asur", library: true);

        var merged = SimilarityService.Merge(source, new[] { R(tmdbCopy, .4), R(libraryCopy, .3) }, Array.Empty<SimilarityResult>(), 10);

        var asur = Assert.Single(merged);
        Assert.Same(libraryCopy, asur.Item);
    }

    [Fact]
    public void NeverRecommendsTheSourceOrAnotherRowOfIt()
    {
        var source = Item(1, 116727, "ODDTAXI", library: true);
        var sourceTmdbCopy = Item(9, 116727, "ODDTAXI");
        var other = Item(2, 5, "Other");

        var merged = SimilarityService.Merge(source, new[] { R(sourceTmdbCopy, .9), R(other, .2) }, Array.Empty<SimilarityResult>(), 10);

        Assert.Equal(new[] { "Other" }, merged.Select(r => r.Item.Title));
    }

    [Fact]
    public void TmdbPicksOutrankGenreMatchesAndAgreementRanksFirst()
    {
        var source = Item(1, 116727, "ODDTAXI");
        var genreMatch = Item(2, 10, "Tulsa King");
        var tmdbOnly = Item(3, 11, "Odd Taxi: In the Woods");
        var both = Item(4, 12, "Monster");

        var merged = SimilarityService.Merge(
            source,
            new[] { R(genreMatch, .5), R(both, .4) },
            new[] { R(tmdbOnly, .9), R(both, .88) },
            10);

        Assert.Equal(new[] { "Monster", "Odd Taxi: In the Woods", "Tulsa King" }, merged.Select(r => r.Item.Title));
    }
}
