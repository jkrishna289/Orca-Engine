using Wholphin.Engine.Embedding;
using Xunit;

namespace Wholphin.Engine.Tests;

/// <summary>Unit tests for the dense content vector.</summary>
public class ContentVectorTests
{
    [Fact]
    public void IdenticalDirection_CosineIsOne()
    {
        var a = ContentVector.Dense(new[] { 3f, 0f, 0f });
        var b = ContentVector.Dense(new[] { 9f, 0f, 0f }); // same direction, different magnitude
        Assert.Equal(1.0, ContentVector.Cosine(a, b), 6);
    }

    [Fact]
    public void Orthogonal_CosineIsZero()
    {
        var a = ContentVector.Dense(new[] { 1f, 0f });
        var b = ContentVector.Dense(new[] { 0f, 1f });
        Assert.Equal(0.0, ContentVector.Cosine(a, b), 6);
    }

    [Fact]
    public void FortyFiveDegrees_CosineIsRootHalf()
    {
        var a = ContentVector.Dense(new[] { 1f, 0f });
        var b = ContentVector.Dense(new[] { 1f, 1f });
        Assert.Equal(0.70710678, ContentVector.Cosine(a, b), 6);
    }

    [Fact]
    public void DifferentDimensions_ScoreZeroRatherThanCrashing()
    {
        // Two models' output reaching one comparison. Scoring 0 keeps it inert; the index-level
        // rule that a snapshot never mixes providers is what stops it happening at all.
        var small = ContentVector.Dense(new[] { 1f, 0f });
        var large = ContentVector.Dense(new[] { 1f, 0f, 0f });

        Assert.Equal(0.0, ContentVector.Cosine(small, large), 6);
    }

    [Fact]
    public void EmptyScoresZeroAgainstAnything()
    {
        var v = ContentVector.Dense(new[] { 1f, 0f });

        Assert.Equal(0.0, ContentVector.Cosine(ContentVector.Empty, v), 6);
        Assert.Equal(0.0, ContentVector.Cosine(v, ContentVector.Empty), 6);
        Assert.Equal(0.0, ContentVector.Cosine(ContentVector.Empty, ContentVector.Empty), 6);
    }

    [Fact]
    public void AnAllZeroVectorIsEmpty_NotADivideByZero()
    {
        Assert.True(ContentVector.Dense(new[] { 0f, 0f, 0f }).IsEmpty);
        Assert.True(ContentVector.Dense(System.Array.Empty<float>()).IsEmpty);
    }

    [Fact]
    public void WeightedMean_OfIdenticalVectors_PointsSameWay()
    {
        var v = ContentVector.Dense(new[] { 1f, 1f, 0f });
        var mean = ContentVector.WeightedMean(new[] { (v, 2.0), (v, 1.0) });

        Assert.Equal(1.0, ContentVector.Cosine(v, mean), 6);
    }

    [Fact]
    public void WeightedMean_LeansTowardTheHeavierVector()
    {
        var a = ContentVector.Dense(new[] { 1f, 0f });
        var b = ContentVector.Dense(new[] { 0f, 1f });
        var mean = ContentVector.WeightedMean(new[] { (a, 9.0), (b, 1.0) });

        Assert.True(ContentVector.Cosine(a, mean) > ContentVector.Cosine(b, mean));
    }

    [Fact]
    public void WeightedMean_SkipsEmptyAndNonPositiveWeights()
    {
        var v = ContentVector.Dense(new[] { 1f, 0f });
        var mean = ContentVector.WeightedMean(new[] { (v, 1.0), (ContentVector.Empty, 5.0), (v, 0.0) });

        Assert.False(mean.IsEmpty);
        Assert.Equal(1.0, ContentVector.Cosine(v, mean), 6);
    }

    [Fact]
    public void WeightedMean_SkipsAVectorFromADifferentModel()
    {
        // A seed embedded by a previous provider must not drag the taste vector onto axes that
        // mean something else — it is skipped, not averaged in.
        var v = ContentVector.Dense(new[] { 1f, 0f });
        var other = ContentVector.Dense(new[] { 1f, 0f, 0f, 0f });
        var mean = ContentVector.WeightedMean(new[] { (v, 1.0), (other, 100.0) });

        Assert.Equal(2, mean.DenseValues!.Count);
        Assert.Equal(1.0, ContentVector.Cosine(v, mean), 6);
    }

    [Fact]
    public void WeightedMean_OfNothingUsable_IsEmpty()
    {
        Assert.True(ContentVector.WeightedMean(new[] { (ContentVector.Empty, 1.0) }).IsEmpty);
        Assert.True(ContentVector.WeightedMean(System.Array.Empty<(ContentVector, double)>()).IsEmpty);
    }
}
