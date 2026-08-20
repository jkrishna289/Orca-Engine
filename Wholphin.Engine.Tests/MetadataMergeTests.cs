using Wholphin.Engine.Configuration;
using Wholphin.Engine.Data.Entities;
using Wholphin.Engine.Metadata;
using Xunit;

namespace Wholphin.Engine.Tests;

/// <summary>
/// Field-level merging: the whole reason the engine gathers PARTIAL answers from several providers
/// rather than picking one winner per title. A regression here is silent — a poster just quietly gets
/// worse — so this is the layer worth pinning down.
/// </summary>
public class MetadataMergeTests
{
    private static MetadataPriority Priority(string? artwork = null, string? core = null, string? ratings = null)
        => MetadataPriority.FromConfig(new PluginConfiguration
        {
            MetadataPriorityArtwork = artwork ?? "fanart,tmdb,tvdb",
            MetadataPriorityCore = core ?? "tmdb,tvdb",
            MetadataPriorityRatings = ratings ?? "omdb,tmdb",
        });

    [Fact]
    public void EachProviderContributesOnlyWhatItHas()
    {
        var merged = MetadataMerge.Merge(
            new MetadataFragment?[]
            {
                new() { Source = "tmdb", Overview = "A team travels through a wormhole.", Genres = new[] { "Sci-Fi" } },
                new() { Source = "omdb", Ratings = new Dictionary<string, double> { ["rt"] = 73 } },
                new() { Source = "fanart", Logo = new ImageCandidate("logo.png", 800, 310, "en", 12) },
            },
            Priority(),
            "en");

        Assert.Equal("A team travels through a wormhole.", merged.Overview);
        Assert.Equal(73, merged.Ratings["rt"]);
        Assert.Equal("logo.png", merged.Logo!.Url);
    }

    [Fact]
    public void ForScalars_TheHigherPriorityProviderWins()
    {
        var merged = MetadataMerge.Merge(
            new MetadataFragment?[]
            {
                new() { Source = "tvdb", Overview = "TVDB's wording." },
                new() { Source = "tmdb", Overview = "TMDB's wording." },
            },
            Priority(core: "tmdb,tvdb"),
            "en");

        Assert.Equal("TMDB's wording.", merged.Overview);
    }

    [Fact]
    public void ALowerPriorityProvider_FillsAFieldTheBetterOneLacks()
    {
        var merged = MetadataMerge.Merge(
            new MetadataFragment?[]
            {
                new() { Source = "tmdb", Overview = null, Genres = new[] { "Drama" } },
                new() { Source = "tvdb", Overview = "TVDB has one." },
            },
            Priority(core: "tmdb,tvdb"),
            "en");

        Assert.Equal("TVDB has one.", merged.Overview);
        Assert.Equal(new[] { "Drama" }, merged.Genres);
    }

    [Fact]
    public void ABetterImage_BeatsAHigherPriorityProvidersWorseOne()
    {
        // The point of scoring rather than ordering: TMDB is listed first here, but its w500 poster
        // must not win over a 1000px one.
        var merged = MetadataMerge.Merge(
            new MetadataFragment?[]
            {
                new() { Source = "tmdb", Poster = new ImageCandidate("tmdb-w500.jpg", 500, 750, null, 0) },
                new() { Source = "fanart", Poster = new ImageCandidate("fanart-1000.png", 1000, 1500, "en", 20) },
            },
            Priority(artwork: "tmdb,fanart"),
            "en");

        Assert.Equal("fanart-1000.png", merged.Poster!.Url);
    }

    [Fact]
    public void AWrongLanguageImage_LosesToACorrectOne_EvenWithFarMoreVotes()
    {
        // A poster with the wrong language's text burned in is WRONG, not just lower quality.
        var merged = MetadataMerge.Merge(
            new MetadataFragment?[]
            {
                new() { Source = "fanart", Poster = new ImageCandidate("german.png", 1000, 1500, "de", 100) },
                new() { Source = "tvdb", Poster = new ImageCandidate("english.png", 1000, 1500, "en", 1) },
            },
            Priority(),
            "en");

        Assert.Equal("english.png", merged.Poster!.Url);
    }

    [Fact]
    public void BetweenEquallyGoodImages_PriorityDecides()
    {
        var identical = new ImageCandidate("a.png", 1000, 1500, "en", 10);
        var merged = MetadataMerge.Merge(
            new MetadataFragment?[]
            {
                new() { Source = "tvdb", Poster = identical with { Url = "tvdb.png" } },
                new() { Source = "fanart", Poster = identical with { Url = "fanart.png" } },
            },
            Priority(artwork: "fanart,tmdb,tvdb"),
            "en");

        Assert.Equal("fanart.png", merged.Poster!.Url);
    }

    [Fact]
    public void RatingsAreUnionedPerKey_NotWonWholesale()
    {
        // OMDb's critic scores and TMDB's community score must coexist.
        var merged = MetadataMerge.Merge(
            new MetadataFragment?[]
            {
                new() { Source = "omdb", Ratings = new Dictionary<string, double> { ["rt"] = 73, ["imdb"] = 87 } },
                new() { Source = "tmdb", Ratings = new Dictionary<string, double> { ["tmdb"] = 86 } },
            },
            Priority(),
            "en");

        Assert.Equal(3, merged.Ratings.Count);
        Assert.Equal(73, merged.Ratings["rt"]);
        Assert.Equal(86, merged.Ratings["tmdb"]);
    }

    [Fact]
    public void ForAContestedRatingKey_TheHigherPriorityProviderWins()
    {
        var merged = MetadataMerge.Merge(
            new MetadataFragment?[]
            {
                new() { Source = "tmdb", Ratings = new Dictionary<string, double> { ["imdb"] = 50 } },
                new() { Source = "omdb", Ratings = new Dictionary<string, double> { ["imdb"] = 87 } },
            },
            Priority(ratings: "omdb,tmdb"),
            "en");

        Assert.Equal(87, merged.Ratings["imdb"]);
    }

    [Fact]
    public void AnUnlistedProvider_StillContributesButRanksLast()
    {
        var merged = MetadataMerge.Merge(
            new MetadataFragment?[]
            {
                new() { Source = "somethingnew", Overview = "From an unlisted provider." },
            },
            Priority(core: "tmdb"),
            "en");

        Assert.Equal("From an unlisted provider.", merged.Overview);
    }

    [Fact]
    public void NoFragments_MergesToAnEmptyRecordRatherThanThrowing()
    {
        var merged = MetadataMerge.Merge(Array.Empty<MetadataFragment?>(), Priority(), "en");

        Assert.Null(merged.Overview);
        Assert.Null(merged.Poster);
        Assert.Empty(merged.Ratings);
    }

    [Fact]
    public void NullFragmentsAreIgnored()
    {
        var merged = MetadataMerge.Merge(
            new MetadataFragment?[] { null, new() { Source = "tmdb", Overview = "Present." }, null },
            Priority(),
            "en");

        Assert.Equal("Present.", merged.Overview);
    }

    [Fact]
    public void ScoreImage_RewardsResolutionAndLanguage()
    {
        var small = MetadataMerge.ScoreImage(new ImageCandidate("a", 500, 750, null, 0), "en", 0);
        var large = MetadataMerge.ScoreImage(new ImageCandidate("b", 2000, 3000, null, 0), "en", 0);
        var localized = MetadataMerge.ScoreImage(new ImageCandidate("c", 500, 750, "en", 0), "en", 0);
        var foreign = MetadataMerge.ScoreImage(new ImageCandidate("d", 500, 750, "de", 0), "en", 0);

        Assert.True(large > small);
        Assert.True(localized > small);
        Assert.True(small > foreign);
    }

    // --- Priority parsing ---------------------------------------------------------------------

    [Fact]
    public void Parse_TrimsLowercasesAndDeduplicates()
    {
        Assert.Equal(new[] { "fanart", "tmdb" }, MetadataPriority.Parse(" Fanart , TMDB , fanart ", "x"));
    }

    [Fact]
    public void Parse_FallsBackToTheDefaultWhenCleared()
    {
        // An empty box never means "no providers for posters".
        Assert.Equal(new[] { "fanart", "tmdb" }, MetadataPriority.Parse("   ", "fanart", "tmdb"));
        Assert.Equal(new[] { "fanart", "tmdb" }, MetadataPriority.Parse(null, "fanart", "tmdb"));
    }

    [Fact]
    public void Rank_OrdersListedProvidersAndBanishesUnlistedOnes()
    {
        var priority = Priority(artwork: "fanart,tmdb");

        Assert.Equal(0, priority.Rank(MetadataCapability.Artwork, "fanart"));
        Assert.Equal(1, priority.Rank(MetadataCapability.Artwork, "tmdb"));
        Assert.True(priority.Rank(MetadataCapability.Artwork, "tvdb") > 1);
    }

    // --- What a row is missing ----------------------------------------------------------------

    [Fact]
    public void MissingCapabilities_NamesExactlyTheAbsentFields()
    {
        var item = new CatalogItem
        {
            Title = "Interstellar",
            Overview = "Present.",
            GenresJson = "[\"Sci-Fi\"]",
            OriginalLanguage = "en",
            PosterImageUrl = "poster.jpg",
            BackdropImageUrl = "backdrop.jpg",
        };

        var missing = MetadataEnricher.MissingCapabilities(item);

        Assert.Equal(MetadataCapability.None, missing & MetadataCapability.Core);
        Assert.Equal(MetadataCapability.None, missing & MetadataCapability.Artwork);
        Assert.Equal(MetadataCapability.Logo, missing & MetadataCapability.Logo);
        Assert.Equal(MetadataCapability.Ratings, missing & MetadataCapability.Ratings);
    }

    [Fact]
    public void MissingCapabilities_AsksForCoreWhenOnlyTheLanguageIsAbsent()
    {
        // A synced library row has an overview and genres from Jellyfin but never a language, so
        // this is the shape that used to look complete and never got asked again.
        var item = new CatalogItem
        {
            Title = "Dangal",
            JellyfinItemId = Guid.NewGuid(),
            Overview = "Present.",
            GenresJson = "[\"Drama\"]",
            OriginalLanguage = null,
        };

        Assert.Equal(MetadataCapability.Core, MetadataEnricher.MissingCapabilities(item) & MetadataCapability.Core);
    }

    [Fact]
    public void Apply_WritesTheOriginalLanguageOntoTheRow()
    {
        var item = new CatalogItem { Title = "Dangal", JellyfinItemId = Guid.NewGuid() };

        Assert.True(MetadataEnricher.Apply(item, new MetadataFragment { OriginalLanguage = "hi" }));
        Assert.Equal("hi", item.OriginalLanguage);
    }

    [Fact]
    public void Apply_NeverOverwritesALanguageTheRowAlreadyHas()
    {
        var item = new CatalogItem { Title = "Dangal", OriginalLanguage = "hi" };

        MetadataEnricher.Apply(item, new MetadataFragment { OriginalLanguage = "en" });

        Assert.Equal("hi", item.OriginalLanguage);
    }

    [Fact]
    public void MissingCapabilities_DoesNotAskForArtworkForALibraryRow()
    {
        // Library items resolve their own art from the Jellyfin id.
        var item = new CatalogItem { Title = "X", JellyfinItemId = Guid.NewGuid() };

        Assert.Equal(MetadataCapability.None, MetadataEnricher.MissingCapabilities(item) & MetadataCapability.Artwork);
    }

    // --- Writing back -------------------------------------------------------------------------

    [Fact]
    public void Apply_FillsEmptyFieldsAndRecordsWhereEachCameFrom()
    {
        var item = new CatalogItem { Title = "Interstellar" };
        var fragment = new MetadataFragment
        {
            Source = "fanart",
            Logo = new ImageCandidate("logo.png", 800, 310, "en", 12),
            Ratings = new Dictionary<string, double> { ["rt"] = 73 },
        };

        Assert.True(MetadataEnricher.Apply(item, fragment));
        Assert.Equal("logo.png", item.LogoImageUrl);
        Assert.Contains("rt", item.RatingsJson!, StringComparison.Ordinal);
        Assert.Contains("Logo", item.MetadataSourcesJson!, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_PopulatesCriticRatingFromRottenTomatoesOnJellyfinsScale()
    {
        // CriticRating is already on the client DTOs, so filling it lights up the UI with no contract
        // change. Jellyfin's scale is 0-10.
        var item = new CatalogItem { Title = "X" };

        MetadataEnricher.Apply(item, new MetadataFragment
        {
            Source = "omdb",
            Ratings = new Dictionary<string, double> { ["rt"] = 73 },
        });

        Assert.Equal(7.3f, item.CriticRating);
    }

    [Fact]
    public void Apply_NeverOverwritesAValueThatIsAlreadyThere()
    {
        var item = new CatalogItem
        {
            Title = "X",
            Overview = "The existing overview.",
            LogoImageUrl = "existing-logo.png",
        };

        MetadataEnricher.Apply(item, new MetadataFragment
        {
            Source = "tvdb",
            Overview = "A replacement.",
            Logo = new ImageCandidate("new-logo.png", 800, 310, "en", 99),
        });

        Assert.Equal("The existing overview.", item.Overview);
        Assert.Equal("existing-logo.png", item.LogoImageUrl);
    }

    [Fact]
    public void Apply_DoesNotWriteArtworkOntoALibraryRow()
    {
        var item = new CatalogItem { Title = "X", JellyfinItemId = Guid.NewGuid() };

        MetadataEnricher.Apply(item, new MetadataFragment
        {
            Source = "fanart",
            Poster = new ImageCandidate("poster.png", 1000, 1500, "en", 5),
        });

        Assert.Null(item.PosterImageUrl);
    }

    [Fact]
    public void Apply_ReportsNoChangeWhenTheFragmentAddsNothing()
    {
        var item = new CatalogItem { Title = "X", Overview = "Present.", GenresJson = "[]" };

        Assert.False(MetadataEnricher.Apply(item, new MetadataFragment { Source = "tmdb" }));
    }

    [Fact]
    public void Apply_SurvivesCorruptProvenanceJson()
    {
        var item = new CatalogItem { Title = "X", MetadataSourcesJson = "{not valid json" };

        Assert.True(MetadataEnricher.Apply(item, new MetadataFragment
        {
            Source = "fanart",
            Logo = new ImageCandidate("logo.png", 800, 310, "en", 1),
        }));
        Assert.Contains("Logo", item.MetadataSourcesJson!, StringComparison.Ordinal);
    }
}
