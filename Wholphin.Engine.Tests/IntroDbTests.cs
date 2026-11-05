using Wholphin.Engine.Metadata;
using Xunit;

namespace Wholphin.Engine.Tests;

/// <summary>
/// Skip times for streamed episodes. Wrong here shows a Skip button that jumps to the wrong place.
/// </summary>
public class IntroDbTests
{
    [Fact]
    public void ReadsMillisecondsAndSecondsAndOpenEnds()
    {
        var s = IntroDb.Parse("""
            {"imdb_id":"tt0903747","season":1,"episode":1,
             "intro":{"start_ms":12000,"end_ms":71500,"confidence":0.9},
             "recap":{"start_sec":0,"end_sec":30.5},
             "outro":{"start_ms":2700000,"end_ms":null},
             "no_intro":false}
            """);

        Assert.Equal(new SkipTimes(12000, 71500), s.Intro);
        Assert.Equal(new SkipTimes(0, 30500), s.Recap);
        Assert.Equal(new SkipTimes(2700000, null), s.Outro);
    }

    [Fact]
    public void MissingOrNonsenseTimes_AreUnknown()
    {
        var s = IntroDb.Parse("""{"intro":null,"recap":{"start_ms":5000,"end_ms":4000},"outro":{"end_ms":10}}""");

        Assert.True(s.IsEmpty);
        Assert.True(IntroDb.Parse("[]").IsEmpty);
    }

    [Fact]
    public void Urls_ForEpisodesAndMovies() =>
        Assert.Equal(
            ["https://api.introdb.app/segments?imdb_id=tt0903747&season=2&episode=10", "https://api.introdb.app/segments?imdb_id=tt1375666&is_movie=true"],
            [IntroDb.Url("tt0903747", 2, 10), IntroDb.Url("tt1375666", null, null)]);
}
