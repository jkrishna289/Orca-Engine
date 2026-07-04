using Wholphin.Engine.Recommendation;
using Xunit;

namespace Wholphin.Engine.Tests;

/// <summary>Unit tests for the content-based item-to-item similarity scorer.</summary>
public class SimilarityScorerTests
{
    [Fact]
    public void IdenticalGenres_ScoreHigh()
    {
        var a = TestData.Item(1, genres: new[] { "Crime", "Thriller" }, year: 2015, rating: 8f);
        var b = TestData.Item(2, genres: new[] { "Crime", "Thriller" }, year: 2016, rating: 8f);

        Assert.True(SimilarityScorer.Score(a, b) > 0.5);
    }

    [Fact]
    public void NoContentOverlap_ScoresZero_EvenWhenEraAndTypeMatch()
    {
        // Same media type + same era + same rating, but zero shared genre/tag/studio/person.
        var a = TestData.Item(1, genres: new[] { "Crime" }, year: 2015, rating: 8f);
        var b = TestData.Item(2, genres: new[] { "Romance" }, year: 2015, rating: 8f);

        Assert.Equal(0.0, SimilarityScorer.Score(a, b), 6);
    }

    [Fact]
    public void SharedPeople_QualifyAsSimilar_EvenWithDifferentGenres()
    {
        var a = TestData.Item(1, genres: new[] { "Sci-Fi" }, people: new[] { "Director:Christopher Nolan" });
        var b = TestData.Item(2, genres: new[] { "Drama" }, people: new[] { "Director:Christopher Nolan" });

        Assert.True(SimilarityScorer.Score(a, b) > 0);
    }

    [Fact]
    public void PartialGenreOverlap_OrdersBelowFullOverlap()
    {
        var source = TestData.Item(1, genres: new[] { "Crime", "Thriller" });
        var full = TestData.Item(2, genres: new[] { "Crime", "Thriller" });
        var partial = TestData.Item(3, genres: new[] { "Crime", "Comedy" });

        Assert.True(SimilarityScorer.Score(source, full) > SimilarityScorer.Score(source, partial));
    }
}
