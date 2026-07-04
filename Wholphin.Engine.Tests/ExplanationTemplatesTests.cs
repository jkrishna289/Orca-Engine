using System.Collections.Generic;
using Wholphin.Engine.Explanation;
using Wholphin.Engine.Personalization;
using Xunit;

namespace Wholphin.Engine.Tests;

/// <summary>Unit tests for the deterministic explanation templates.</summary>
public class ExplanationTemplatesTests
{
    [Fact]
    public void TasteMatch_NamesSharedTopGenres()
    {
        var reason = ExplanationTemplates.TasteMatch(
            new[] { "Crime", "Thriller", "Drama" },
            new List<string> { "Crime", "Thriller" });
        Assert.Equal("Matches your taste for crime and thriller", reason);
    }

    [Fact]
    public void TasteMatch_NoOverlap_FallsBack()
    {
        var reason = ExplanationTemplates.TasteMatch(new[] { "Western" }, new List<string> { "Crime" });
        Assert.Equal("Matches your taste", reason);
    }

    [Fact]
    public void Recommendation_PrefersLovedGenre()
    {
        var affinity = new AffinityVector();
        affinity.Genre["Science Fiction"] = 0.9;
        var item = TestData.Item(genres: new[] { "Science Fiction" }, rating: 6f);

        Assert.Equal("Because you enjoy Science Fiction", ExplanationTemplates.Recommendation(item, affinity));
    }

    [Fact]
    public void Recommendation_UsesLikedPerson_WhenStrongerThanGenre()
    {
        var affinity = new AffinityVector();
        affinity.Genre["Drama"] = 0.2;
        affinity.Person["Director:Christopher Nolan"] = 0.95;
        var item = TestData.Item(genres: new[] { "Drama" }, people: new[] { "Director:Christopher Nolan" }, rating: 6f);

        Assert.Equal("Because you like Christopher Nolan", ExplanationTemplates.Recommendation(item, affinity));
    }

    [Fact]
    public void Recommendation_HighlyRated_WhenNoTasteSignal()
    {
        var item = TestData.Item(genres: new[] { "Documentary" }, rating: 8.5f);
        Assert.Equal("Highly rated", ExplanationTemplates.Recommendation(item, new AffinityVector()));
    }

    [Fact]
    public void Recommendation_AlwaysReturnsSomething()
    {
        var item = TestData.Item(genres: new[] { "Documentary" }, rating: 4f);
        Assert.Equal("Picked for you", ExplanationTemplates.Recommendation(item, new AffinityVector()));
    }
}
