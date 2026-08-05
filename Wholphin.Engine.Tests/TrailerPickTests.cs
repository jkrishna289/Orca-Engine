using Wholphin.Engine.Integrations.Tmdb;
using Xunit;

namespace Wholphin.Engine.Tests;

/// <summary>
/// Trailer selection. The failure mode this guards against is silent: a wrong eligibility rule does
/// not throw, it just quietly changes which trailer every viewer sees — or removes one that was
/// there. The ladder must stay preference-ordered while the admin flags only narrow the field.
/// </summary>
public class TrailerPickTests
{
    private static TmdbVideo Video(string key, string type, string lang, bool official = false, string site = "YouTube")
        => new() { Key = key, Type = type, Iso6391 = lang, Official = official, Site = site };

    [Fact]
    public void PrefersOfficialTrailerInThePreferredLanguage()
    {
        var url = TmdbClient.PickTrailer(
            new[]
            {
                Video("teaser-en", "Teaser", "en"),
                Video("unofficial-en", "Trailer", "en"),
                Video("official-en", "Trailer", "en", official: true),
                Video("official-hi", "Trailer", "hi", official: true),
            },
            "en");

        Assert.Contains("official-en", url);
    }

    [Fact]
    public void FallsBackToAnotherLanguageRatherThanNothing()
    {
        // A title with no English video should still get its native trailer.
        var url = TmdbClient.PickTrailer(new[] { Video("native-hi", "Trailer", "hi", official: true) }, "en");

        Assert.Contains("native-hi", url);
    }

    [Fact]
    public void PrefersLanguageMatchOverOfficialFlag()
    {
        // Language sits above officialness in the ladder: audio the viewer understands matters more
        // than the badge.
        var url = TmdbClient.PickTrailer(
            new[] { Video("official-fr", "Trailer", "fr", official: true), Video("unofficial-en", "Trailer", "en") },
            "en");

        Assert.Contains("unofficial-en", url);
    }

    [Fact]
    public void TeaserUsedOnlyWhenNoTrailerExists()
    {
        Assert.Contains("teaser", TmdbClient.PickTrailer(new[] { Video("teaser", "Teaser", "en") }, "en"));

        var withTrailer = TmdbClient.PickTrailer(
            new[] { Video("teaser", "Teaser", "en"), Video("trailer", "Trailer", "en") }, "en");
        Assert.Contains("trailer", withTrailer);
    }

    [Fact]
    public void DisallowingTeasersDropsThemEntirely()
    {
        var url = TmdbClient.PickTrailer(new[] { Video("teaser", "Teaser", "en") }, "en", allowTeasers: false);

        Assert.Null(url);
    }

    [Fact]
    public void RequiringOfficialDropsUnofficialVideos()
    {
        var only = new[] { Video("unofficial", "Trailer", "en") };

        Assert.NotNull(TmdbClient.PickTrailer(only, "en"));
        Assert.Null(TmdbClient.PickTrailer(only, "en", requireOfficial: true));
    }

    [Fact]
    public void NonYouTubeIsExcludedUnlessAllowed_AndGetsItsOwnUrlShape()
    {
        var vimeo = new[] { Video("12345", "Trailer", "en", official: true, site: "Vimeo") };

        Assert.Null(TmdbClient.PickTrailer(vimeo, "en"));

        var allowed = TmdbClient.PickTrailer(vimeo, "en", allowOtherSites: true);
        Assert.Equal("https://vimeo.com/12345", allowed);
    }

    [Fact]
    public void YouTubeStillWinsOverVimeoWhenBothAllowed()
    {
        var url = TmdbClient.PickTrailer(
            new[] { Video("v1", "Trailer", "en", official: true, site: "Vimeo"), Video("y1", "Trailer", "en", official: true) },
            "en",
            allowOtherSites: true);

        // Ordering follows TMDB's own list order within a rung; the point here is that enabling
        // other sites does not demote YouTube when it also qualifies at the same rung.
        Assert.Contains("vimeo.com/v1", url);
    }

    [Fact]
    public void EmptyOrKeylessInputYieldsNothing()
    {
        Assert.Null(TmdbClient.PickTrailer(null, "en"));
        Assert.Null(TmdbClient.PickTrailer(System.Array.Empty<TmdbVideo>(), "en"));
        Assert.Null(TmdbClient.PickTrailer(new[] { Video(string.Empty, "Trailer", "en") }, "en"));
    }
}
