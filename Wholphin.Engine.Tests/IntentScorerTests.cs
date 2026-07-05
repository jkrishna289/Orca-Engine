using Wholphin.Engine.Data.Entities;
using Wholphin.Engine.Intelligence;
using Wholphin.Engine.Personalization;
using Xunit;

namespace Wholphin.Engine.Tests;

/// <summary>Unit tests for the intent scorer that fronts the affinity math.</summary>
public class IntentScorerTests
{
    [Fact]
    public void Score_SumsGenreAffinity()
    {
        var affinity = new AffinityVector();
        affinity.Genre["Science Fiction"] = 0.9;
        var scorer = new IntentScorer(affinity);

        var item = new CatalogItem { GenresJson = "[\"Science Fiction\"]" };

        Assert.Equal(0.9, scorer.Score(item), 3);
    }

    [Fact]
    public void EmptyAffinity_ScoresZero()
    {
        var scorer = new IntentScorer(new AffinityVector());

        Assert.Equal(0, scorer.Score(new CatalogItem { GenresJson = "[\"Drama\"]" }));
    }

    [Fact]
    public void FollowsFranchise_TrueAboveThreshold()
    {
        var affinity = new AffinityVector();
        affinity.Franchise["The Dark Knight Collection"] = 0.5;
        var scorer = new IntentScorer(affinity);

        Assert.True(scorer.FollowsFranchise(new CatalogItem { CollectionName = "The Dark Knight Collection" }));
    }

    [Fact]
    public void FollowsFranchise_FalseBelowThreshold()
    {
        var affinity = new AffinityVector();
        affinity.Franchise["Weak"] = 0.05;
        var scorer = new IntentScorer(affinity);

        Assert.False(scorer.FollowsFranchise(new CatalogItem { CollectionName = "Weak" }));
    }

    [Fact]
    public void FollowsFranchise_FalseWhenNoCollection()
    {
        var affinity = new AffinityVector();
        affinity.Franchise["Whatever"] = 0.9;
        var scorer = new IntentScorer(affinity);

        Assert.False(scorer.FollowsFranchise(new CatalogItem()));
    }

    [Fact]
    public void Confidence_PassesThrough()
    {
        var scorer = new IntentScorer(new AffinityVector { Confidence = 0.62 });

        Assert.Equal(0.62, scorer.Confidence);
    }
}
