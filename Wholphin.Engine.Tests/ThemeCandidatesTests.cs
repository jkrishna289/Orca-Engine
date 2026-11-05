using Wholphin.Engine.Themes;
using Wholphin.Engine.Trailer;
using Xunit;

namespace Wholphin.Engine.Tests;

/// <summary>
/// Picking a show's theme out of YouTube search results. Wrong here plays a review or a trailer's
/// voice-over on the show's page, every time it opens.
/// </summary>
public class ThemeCandidatesTests
{
    private static TrailerCandidate C(string id, string title, int? seconds = 150) => new(id, title, null, seconds);

    [Fact]
    public void PicksTheTitleSong_OverTrailersReviewsAndCovers()
    {
        var best = ThemeCandidates.Best(
            [
                C("trailer", "Paatal Lok Season 2 | Official Trailer"),
                C("review", "Paatal Lok Review - Is the title track good?"),
                C("cover", "Paatal Lok Title Track (Piano Cover)"),
                C("song", "Paatal Lok - Title Track | Amazon Prime Video"),
            ],
            "Paatal Lok");

        Assert.Equal("song", best?.Id);
    }

    [Fact]
    public void NeedsTheShowsNameAndAThemeWord()
    {
        Assert.Null(ThemeCandidates.Best([C("other", "Fortnite x The Simpsons theme song")], "Family Guy"));
        Assert.Null(ThemeCandidates.Best([C("clip", "Family Guy - Stewie's best moments")], "Family Guy"));
        Assert.Equal("ok", ThemeCandidates.Best([C("ok", "Grey's Anatomy Theme Song")], "Greys Anatomy")?.Id);
    }

    [Fact]
    public void SkipsCompilationsAndSnippets()
    {
        Assert.Null(ThemeCandidates.Best([C("long", "Dark OST full soundtrack", 3600)], "Dark"));
        Assert.Null(ThemeCandidates.Best([C("short", "Dark theme song", 8)], "Dark"));
        Assert.Equal("unknown", ThemeCandidates.Best([C("unknown", "Dark - Opening Theme", null)], "Dark")?.Id);
    }
}
