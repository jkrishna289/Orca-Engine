using Wholphin.Engine.Trailer;
using Wholphin.Engine.Trailer.Sources;
using Xunit;

namespace Wholphin.Engine.Tests;

/// <summary>
/// The scoring that makes the YouTube search source safe to enable. Its blind predecessor handed the
/// first search result straight to the downloader, which is why it shipped off: a confidently wrong
/// trailer is worse than none. Every test here is really the same question — would this have picked
/// the reaction video?
/// </summary>
public class TrailerSearchScoreTests
{
    private static readonly TrailerScoreWeights Weights = TrailerScoreWeights.Default;
    private const int MinScore = 40;

    private static TrailerCandidate Candidate(string title, string? channel = null, int? duration = 150)
        => new("vid1", title, channel, duration);

    [Fact]
    public void TheRealOfficialTrailer_Wins()
    {
        var candidates = new[]
        {
            new TrailerCandidate("a", "Dune Part Two REACTION!! First Time Watching", "SomeGuyReacts", 1200),
            new TrailerCandidate("b", "Dune: Part Two | Official Trailer 2024", "Warner Bros. Pictures", 170),
            new TrailerCandidate("c", "Dune Part Two ENDING EXPLAINED", "FilmBreakdown", 900),
        };

        var best = TrailerCandidateScorer.Best(candidates, "Dune: Part Two", 2024, Weights, MinScore);

        Assert.Equal("b", best!.Value.Id);
    }

    [Fact]
    public void AVideoNotNamingTheTitle_IsPenalised()
    {
        var related = TrailerCandidateScorer.Score(Candidate("Official Trailer", "Warner Bros. Pictures"), "Interstellar", 2014, Weights);
        var named = TrailerCandidateScorer.Score(Candidate("Interstellar Official Trailer", "Warner Bros. Pictures"), "Interstellar", 2014, Weights);

        Assert.True(named > related);
    }

    [Fact]
    public void PunctuationAndCaseDoNotBreakTheTitleMatch()
    {
        var score = TrailerCandidateScorer.Score(
            Candidate("SPIDER-MAN: NO WAY HOME - Official Trailer (HD)", "Sony Pictures Entertainment"),
            "Spider Man No Way Home",
            null,
            Weights);

        Assert.True(score >= MinScore);
    }

    [Fact]
    public void AFanTrailer_FallsBelowTheThreshold()
    {
        var candidates = new[] { Candidate("Interstellar 2 - Fan Made Concept Trailer", "MovieFanEdits") };

        Assert.Null(TrailerCandidateScorer.Best(candidates, "Interstellar", 2014, Weights, MinScore));
    }

    [Fact]
    public void NoCandidateClearingTheBar_ReturnsNull_RatherThanTheLeastBad()
    {
        // Declining is the whole reason this source is safe to have on by default.
        var candidates = new[]
        {
            Candidate("Top 10 Space Movies", "ListChannel"),
            Candidate("Everything Wrong With Space Films", "CinemaSins"),
        };

        Assert.Null(TrailerCandidateScorer.Best(candidates, "Interstellar", 2014, Weights, MinScore));
    }

    [Fact]
    public void AYearTwoOrMoreOff_IsNearFatal()
    {
        var right = TrailerCandidateScorer.Score(Candidate("Suspiria Official Trailer 2018"), "Suspiria", 2018, Weights);
        var wrong = TrailerCandidateScorer.Score(Candidate("Suspiria Official Trailer 1977"), "Suspiria", 2018, Weights);

        Assert.True(right > wrong);
        Assert.True(wrong < MinScore);
    }

    [Fact]
    public void ANeighbouringYear_IsToleratedRatherThanPunished()
    {
        // Marketing routinely straddles a release year; only a two-year gap means a different film.
        var score = TrailerCandidateScorer.Score(
            Candidate("Interstellar Official Trailer 2014", "Paramount Pictures"),
            "Interstellar",
            2015,
            Weights);

        Assert.True(score >= MinScore);
    }

    [Theory]
    [InlineData(8)]      // a fragment
    [InlineData(2400)]   // a 40-minute breakdown
    public void ImplausibleDurations_AreDocked(int seconds)
    {
        var normal = TrailerCandidateScorer.Score(Candidate("Dune Official Trailer", "Warner Bros. Pictures", 150), "Dune", null, Weights);
        var odd = TrailerCandidateScorer.Score(Candidate("Dune Official Trailer", "Warner Bros. Pictures", seconds), "Dune", null, Weights);

        Assert.True(odd < normal);
    }

    [Fact]
    public void AMissingDuration_CostsNothing()
    {
        // yt-dlp's flat-playlist output omits duration for some results; that must not disqualify them.
        var known = TrailerCandidateScorer.Score(Candidate("Dune Official Trailer", "Warner Bros. Pictures", 150), "Dune", null, Weights);
        var unknown = TrailerCandidateScorer.Score(Candidate("Dune Official Trailer", "Warner Bros. Pictures", null), "Dune", null, Weights);

        Assert.Equal(known, unknown);
    }

    [Fact]
    public void AChannelNamedAfterTheTitle_CountsAsOfficial()
    {
        var franchise = TrailerCandidateScorer.Score(Candidate("The Witcher Official Trailer", "The Witcher"), "The Witcher", null, Weights);
        var stranger = TrailerCandidateScorer.Score(Candidate("The Witcher Official Trailer", "randomuploader91"), "The Witcher", null, Weights);

        Assert.True(franchise > stranger);
    }

    [Fact]
    public void ExtractYears_IgnoresResolutionsAndLongNumbers()
    {
        Assert.Empty(TrailerCandidateScorer.ExtractYears("Trailer 1080p"));
        Assert.Empty(TrailerCandidateScorer.ExtractYears("24000 views"));
        Assert.Contains(2014, TrailerCandidateScorer.ExtractYears("Interstellar (2014) Trailer"));
    }

    [Fact]
    public void ParseLines_ReadsTabSeparatedOutput()
    {
        var stdout = "abc123\tDune Official Trailer\tWarner Bros. Pictures\t170\n"
                   + "def456\tDune Reaction\tSomeGuy\t900\n";

        var parsed = YtDlpSearchTrailerSource.ParseLines(stdout);

        Assert.Equal(2, parsed.Count);
        Assert.Equal("abc123", parsed[0].Id);
        Assert.Equal("Warner Bros. Pictures", parsed[0].Channel);
        Assert.Equal(170, parsed[0].DurationSeconds);
    }

    [Fact]
    public void ParseLines_SkipsMalformedRowsRatherThanFailingTheWholeSearch()
    {
        var stdout = "\n"
                   + "onlyoneField\n"
                   + "NA\tSomething\tChannel\t100\n"
                   + "good1\tDune Official Trailer\tWarner Bros. Pictures\t170\n";

        var parsed = YtDlpSearchTrailerSource.ParseLines(stdout);

        Assert.Single(parsed);
        Assert.Equal("good1", parsed[0].Id);
    }

    [Fact]
    public void ParseLines_TreatsNaFieldsAsUnknown()
    {
        var parsed = YtDlpSearchTrailerSource.ParseLines("id1\tSome Trailer\tNA\tNA\n");

        Assert.Null(parsed[0].Channel);
        Assert.Null(parsed[0].DurationSeconds);
    }

    [Fact]
    public void ParseLines_HandlesEmptyOutput()
    {
        Assert.Empty(YtDlpSearchTrailerSource.ParseLines(null));
        Assert.Empty(YtDlpSearchTrailerSource.ParseLines("   "));
    }

    [Fact]
    public void BuildQuery_AsksForNCandidatesAndIncludesTheYear()
    {
        Assert.Equal("ytsearch8:Interstellar 2014 official trailer", YtDlpSearchTrailerSource.BuildQuery("Interstellar", 2014, 8));
        Assert.Equal("ytsearch5:Interstellar official trailer", YtDlpSearchTrailerSource.BuildQuery("Interstellar", null, 5));
    }

    [Fact]
    public void BuildQuery_StripsQuotesThatWouldBreakTheArgumentString()
    {
        Assert.DoesNotContain('"', YtDlpSearchTrailerSource.BuildQuery("The \"Burbs\"", 1989, 8));
    }
}
