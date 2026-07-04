using System.Collections.Generic;
using Wholphin.Engine.Data.Entities;
using Wholphin.Engine.Personalization;
using Xunit;

namespace Wholphin.Engine.Tests;

/// <summary>Unit tests for catalog-feature extraction and affinity scoring (incl. the v8 scalar dims).</summary>
public class CatalogFeaturesTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData(0, null)]
    [InlineData(80, "short")]
    [InlineData(105, "medium")]
    [InlineData(140, "long")]
    [InlineData(180, "epic")]
    public void RuntimeBucket_Classifies(int? minutes, string? expected)
    {
        Assert.Equal(expected, CatalogFeatures.RuntimeBucket(minutes));
    }

    [Fact]
    public void ForEachScalarFeature_EmitsPresentDimensions()
    {
        var item = new CatalogItem
        {
            RuntimeMinutes = 100,
            OfficialRating = "PG-13",
            OriginalLanguage = "hi",
            CollectionName = "The Dark Knight Collection",
        };

        var emitted = new Dictionary<ScalarDimension, string>();
        CatalogFeatures.ForEachScalarFeature(item, (dim, value) => emitted[dim] = value);

        Assert.Equal("medium", emitted[ScalarDimension.Runtime]);
        Assert.Equal("PG-13", emitted[ScalarDimension.Maturity]);
        Assert.Equal("hi", emitted[ScalarDimension.Language]);
        Assert.Equal("The Dark Knight Collection", emitted[ScalarDimension.Franchise]);
    }

    [Fact]
    public void ForEachScalarFeature_SkipsMissingDimensions()
    {
        var item = new CatalogItem { RuntimeMinutes = 100 }; // only runtime present
        var count = 0;
        CatalogFeatures.ForEachScalarFeature(item, (_, _) => count++);
        Assert.Equal(1, count);
    }

    [Fact]
    public void Dot_IncludesScalarDimensions()
    {
        var affinity = new AffinityVector();
        affinity.Language["hi"] = 0.8;
        affinity.Runtime["epic"] = 0.5;

        var item = new CatalogItem
        {
            MediaType = Wholphin.Engine.Data.Enums.MediaType.Movie,
            OriginalLanguage = "hi",
            RuntimeMinutes = 175, // epic
        };

        // Language 0.8 + runtime 0.5 (+ MediaType "Movie" default 0) = 1.3.
        Assert.Equal(1.3, CatalogFeatures.Dot(affinity, item), 6);
    }
}
