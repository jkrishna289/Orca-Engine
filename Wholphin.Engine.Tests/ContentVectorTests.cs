using System.Collections.Generic;
using Wholphin.Engine.Embedding;
using Xunit;

namespace Wholphin.Engine.Tests;

/// <summary>Unit tests for the unified sparse/dense content vector.</summary>
public class ContentVectorTests
{
    [Fact]
    public void Dense_IdenticalDirection_CosineIsOne()
    {
        var a = ContentVector.Dense(new[] { 3f, 0f, 0f });
        var b = ContentVector.Dense(new[] { 9f, 0f, 0f }); // same direction, different magnitude
        Assert.Equal(1.0, ContentVector.Cosine(a, b), 6);
    }

    [Fact]
    public void Dense_Orthogonal_CosineIsZero()
    {
        var a = ContentVector.Dense(new[] { 1f, 0f });
        var b = ContentVector.Dense(new[] { 0f, 1f });
        Assert.Equal(0.0, ContentVector.Cosine(a, b), 6);
    }

    [Fact]
    public void Dense_FortyFiveDegrees_CosineIsRootHalf()
    {
        var a = ContentVector.Dense(new[] { 1f, 0f });
        var b = ContentVector.Dense(new[] { 1f, 1f });
        Assert.Equal(0.70710678, ContentVector.Cosine(a, b), 6);
    }

    [Fact]
    public void MixedKind_And_Empty_ScoreZero()
    {
        var sparse = ContentVector.Sparse(new Dictionary<string, double> { ["a"] = 1.0 });
        var dense = ContentVector.Dense(new[] { 1f });
        Assert.Equal(0.0, ContentVector.Cosine(sparse, dense), 6);
        Assert.Equal(0.0, ContentVector.Cosine(ContentVector.Empty, sparse), 6);
    }

    [Fact]
    public void WeightedMean_OfIdenticalVectors_PointsSameWay()
    {
        var v = ContentVector.Sparse(Normalize(new Dictionary<string, double> { ["crime"] = 1.0, ["thriller"] = 1.0 }));
        var mean = ContentVector.WeightedMean(new[] { (v, 2.0), (v, 1.0) });
        Assert.Equal(1.0, ContentVector.Cosine(v, mean), 6);
    }

    [Fact]
    public void WeightedMean_SkipsEmptyAndNonPositiveWeights()
    {
        var v = ContentVector.Sparse(Normalize(new Dictionary<string, double> { ["a"] = 1.0 }));
        var mean = ContentVector.WeightedMean(new[] { (v, 1.0), (ContentVector.Empty, 5.0), (v, 0.0) });
        Assert.False(mean.IsEmpty);
        Assert.Equal(1.0, ContentVector.Cosine(v, mean), 6);
    }

    [Fact]
    public void WeightedMean_OfNothingUsable_IsEmpty()
    {
        var mean = ContentVector.WeightedMean(new[] { (ContentVector.Empty, 1.0) });
        Assert.True(mean.IsEmpty);
    }

    private static Dictionary<string, double> Normalize(Dictionary<string, double> raw)
    {
        var sumSq = 0.0;
        foreach (var v in raw.Values)
        {
            sumSq += v * v;
        }

        var norm = System.Math.Sqrt(sumSq);
        var result = new Dictionary<string, double>(raw.Count);
        foreach (var (k, v) in raw)
        {
            result[k] = v / norm;
        }

        return result;
    }
}
