using Wholphin.Engine.Personalization;
using Wholphin.Engine.Recommendation;
using Xunit;

namespace Wholphin.Engine.Tests;

/// <summary>Unit tests for the novelty/exploration scorer.</summary>
public class ExplorationScorerTests
{
    private static AffinityVector Affinity()
    {
        var v = new AffinityVector();
        v.Genre["Action"] = 1.0;    // loved
        v.Genre["Horror"] = -0.5;   // disliked
        return v;
    }

    [Fact]
    public void UnknownGenre_HighQuality_IsNovel()
    {
        var item = TestData.Item(genres: new[] { "Documentary" }, rating: 8f);
        Assert.Equal(0.8, ExplorationScorer.Novelty(item, Affinity()), 6);
    }

    [Fact]
    public void LovedGenre_IsNotNovel()
    {
        var item = TestData.Item(genres: new[] { "Action" }, rating: 9f);
        Assert.Equal(0.0, ExplorationScorer.Novelty(item, Affinity()), 6);
    }

    [Fact]
    public void DislikedGenre_IsNotExplored()
    {
        var item = TestData.Item(genres: new[] { "Horror" }, rating: 9f);
        Assert.Equal(0.0, ExplorationScorer.Novelty(item, Affinity()), 6);
    }

    [Fact]
    public void Unrated_IsNotNovel()
    {
        var item = TestData.Item(genres: new[] { "Documentary" }, rating: null);
        Assert.Equal(0.0, ExplorationScorer.Novelty(item, Affinity()), 6);
    }

    [Fact]
    public void Genreless_IsNotNovel()
    {
        var item = TestData.Item(genres: null, rating: 9f);
        Assert.Equal(0.0, ExplorationScorer.Novelty(item, Affinity()), 6);
    }
}
