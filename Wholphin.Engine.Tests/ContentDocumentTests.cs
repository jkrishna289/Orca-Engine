using Wholphin.Engine.Data.Entities;
using Wholphin.Engine.Data.Enums;
using Wholphin.Engine.Embedding;
using Wholphin.Engine.Integrations.Jellyseerr;
using Xunit;

namespace Wholphin.Engine.Tests;

/// <summary>
/// The text every embedding provider actually sees. The language assertions are the load-bearing
/// ones: TMDB overviews are English whatever the film, so without an explicit language the document
/// was origin-blind and a viewer whose taste is non-English got matched on genre alone.
/// </summary>
public class ContentDocumentTests
{
    private static CatalogItem Film(string title, string? language) => new()
    {
        Title = title,
        MediaType = MediaType.Movie,
        ProductionYear = 2016,
        OriginalLanguage = language,
        GenresJson = """["Drama","Romance"]""",
        Overview = "A sweeping story about a family.",
    };

    [Fact]
    public void CarriesBothTheCodeAndTheEnglishName()
    {
        var doc = ContentDocument.Of(Film("Dangal", "hi"));

        Assert.Contains("hi", doc, StringComparison.Ordinal);
        Assert.Contains("Hindi", doc, StringComparison.Ordinal);
    }

    [Fact]
    public void SameGenresDifferentOriginNoLongerReadAsTheSameThing()
    {
        var hindi = ContentDocument.Of(Film("Dangal", "hi"));
        var english = ContentDocument.Of(Film("Brooklyn", "en"));

        Assert.NotEqual(hindi, english);
        Assert.Contains("Hindi", hindi, StringComparison.Ordinal);
        Assert.DoesNotContain("Hindi", english, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingLanguageSimplyOmitsTheSection()
    {
        var doc = ContentDocument.Of(Film("Unknown Origin", null));

        Assert.DoesNotContain("Language:", doc, StringComparison.Ordinal);
        Assert.Contains("Genres:", doc, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnrecognizedCodeStillContributesItself()
    {
        var doc = ContentDocument.Of(Film("Odd", "zzzz"));

        Assert.Contains("Language: zzzz.", doc, StringComparison.Ordinal);
    }

    [Fact]
    public void ExternalResultsUseTheSameLabelInTheSamePlace()
    {
        var catalog = ContentDocument.Of(Film("Dangal", "hi"));
        var external = ContentDocument.Of(new DiscoverResult
        {
            Title = "Dangal",
            Year = 2016,
            MediaType = MediaType.Movie,
            OriginalLanguage = "hi",
            Genres = new List<string> { "Drama", "Romance" },
            Overview = "A sweeping story about a family.",
        });

        // Seeds and candidates are embedded in one batch; a label the two overloads disagree on
        // becomes a term that only ever appears on one side of the comparison.
        Assert.Contains("Language: hi, Hindi.", catalog, StringComparison.Ordinal);
        Assert.Contains("Language: hi, Hindi.", external, StringComparison.Ordinal);
        Assert.True(catalog.IndexOf("Language:", StringComparison.Ordinal)
            < catalog.IndexOf("Genres:", StringComparison.Ordinal));
        Assert.True(external.IndexOf("Language:", StringComparison.Ordinal)
            < external.IndexOf("Genres:", StringComparison.Ordinal));
    }
}
