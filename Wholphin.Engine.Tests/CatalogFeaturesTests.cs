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

        // Language 0.8 at weight 1.0 + runtime 0.5 at weight 0.2 (+ MediaType "Movie" default 0) = 0.9.
        Assert.Equal(0.9, CatalogFeatures.Dot(affinity, item), 6);
    }

    [Fact]
    public void Dot_DoesNotRewardATitleForCarryingMoreMetadata()
    {
        // Same taste match per entry; one title just has a much longer tag list. It used to win by
        // summing every tag, so well-documented titles beat sparsely tagged ones for no reason.
        var affinity = new AffinityVector();
        affinity.Genre["Drama"] = 1.0;
        for (var i = 0; i < 40; i++)
        {
            affinity.Tag["tag" + i] = 0.3;
        }

        var sparse = new CatalogItem { GenresJson = "[\"Drama\"]", TagsJson = "[\"tag0\",\"tag1\"]" };
        var rich = new CatalogItem
        {
            GenresJson = "[\"Drama\"]",
            TagsJson = System.Text.Json.JsonSerializer.Serialize(Enumerable.Range(0, 40).Select(i => "tag" + i)),
        };

        Assert.Equal(CatalogFeatures.Dot(affinity, sparse), CatalogFeatures.Dot(affinity, rich), 6);
    }

    [Fact]
    public void Dot_LetsLanguageSeparateTitlesOfTheSameGenres()
    {
        // A Hindi-film viewer: the Hindi drama must beat a Korean drama with extra genres and tags.
        var affinity = new AffinityVector();
        affinity.Genre["Drama"] = 1.0;
        affinity.Genre["Romance"] = 0.7;
        affinity.Genre["Comedy"] = 0.6;
        affinity.Language["hi"] = 1.0;
        affinity.Tag["romance"] = 0.4;

        var hindi = new CatalogItem { GenresJson = "[\"Drama\",\"Romance\"]", OriginalLanguage = "hi" };
        var korean = new CatalogItem
        {
            GenresJson = "[\"Drama\",\"Romance\",\"Comedy\"]",
            OriginalLanguage = "ko",
            TagsJson = "[\"romance\",\"romance\",\"romance\"]",
        };

        Assert.True(CatalogFeatures.Dot(affinity, hindi) > CatalogFeatures.Dot(affinity, korean));
    }
}
