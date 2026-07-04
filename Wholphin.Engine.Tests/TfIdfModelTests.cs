using System.Collections.Generic;
using Wholphin.Engine.Recommendation;
using Xunit;

namespace Wholphin.Engine.Tests;

/// <summary>Unit tests for the pure TF-IDF vector-space model.</summary>
public class TfIdfModelTests
{
    private static TfIdfModel BuildModel() => TfIdfModel.Build(new IReadOnlyCollection<string>[]
    {
        new[] { "crime", "thriller", "gritty" },
        new[] { "crime", "drama" },
        new[] { "romance", "comedy" },
        new[] { "romance", "drama" },
    });

    [Fact]
    public void IdenticalDocuments_CosineIsOne()
    {
        var model = BuildModel();
        var a = model.Vectorize(new[] { "crime", "thriller" });
        var b = model.Vectorize(new[] { "crime", "thriller" });
        Assert.Equal(1.0, TfIdfModel.Cosine(a, b), 6);
    }

    [Fact]
    public void DisjointDocuments_CosineIsZero()
    {
        var model = BuildModel();
        var a = model.Vectorize(new[] { "crime", "thriller" });
        var b = model.Vectorize(new[] { "romance", "comedy" });
        Assert.Equal(0.0, TfIdfModel.Cosine(a, b), 6);
    }

    [Fact]
    public void RareSharedTerm_OutweighsCommonSharedTerm()
    {
        var model = BuildModel();
        var source = model.Vectorize(new[] { "crime", "thriller", "drama" });
        var sharesRare = model.Vectorize(new[] { "thriller" }); // df=1, high idf
        var sharesCommon = model.Vectorize(new[] { "drama" });  // df=2, lower idf

        Assert.True(TfIdfModel.Cosine(source, sharesRare) > TfIdfModel.Cosine(source, sharesCommon));
    }

    [Fact]
    public void OutOfVocabularyTerms_AreIgnored()
    {
        var model = BuildModel();
        var vector = model.Vectorize(new[] { "nonexistentterm" });
        Assert.Empty(vector);
    }

    [Fact]
    public void EmptyDocument_CosineIsZero()
    {
        var model = BuildModel();
        var empty = model.Vectorize(System.Array.Empty<string>());
        var real = model.Vectorize(new[] { "crime" });
        Assert.Equal(0.0, TfIdfModel.Cosine(empty, real), 6);
    }
}
