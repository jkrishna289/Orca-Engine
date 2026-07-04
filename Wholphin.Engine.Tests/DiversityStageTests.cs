using System.Collections.Generic;
using System.Linq;
using Wholphin.Engine.Discovery;
using Xunit;

namespace Wholphin.Engine.Tests;

/// <summary>Unit tests for the diversity reshuffle stage.</summary>
public class DiversityStageTests
{
    [Fact]
    public void NeverDropsCandidates()
    {
        var stage = new DiversityStage(new Wholphin.Engine.Ranking.ScoringPolicy());
        var input = new List<ScoredCandidate>
        {
            TestData.Scored(1, "A", 1.0, "Crime"),
            TestData.Scored(2, "B", 0.9, "Crime"),
            TestData.Scored(3, "C", 0.8, "Crime"),
        };

        var result = stage.Apply(input);

        Assert.Equal(3, result.Ranked.Count);
        Assert.Equal(new[] { 1, 2, 3 }, result.Ranked.Select(r => r.Candidate.Result.TmdbId).OrderBy(x => x));
    }

    [Fact]
    public void OverrepresentedGenre_IsPenalized_SoAnotherGenreLeapfrogs()
    {
        var stage = new DiversityStage(new Wholphin.Engine.Ranking.ScoringPolicy());
        var input = new List<ScoredCandidate>
        {
            TestData.Scored(1, "A", 1.00, "Crime"),
            TestData.Scored(2, "B", 0.90, "Crime"),
            TestData.Scored(3, "C", 0.80, "Crime"),  // 3rd crime → penalized
            TestData.Scored(4, "D", 0.78, "Comedy"), // lower raw, but diverse
        };

        var result = stage.Apply(input);

        // A, B (crime) lead; then the comedy leapfrogs the penalized 3rd crime.
        Assert.Equal(4, result.Ranked.Count);
        Assert.Equal(4, result.Ranked[2].Candidate.Result.TmdbId);
        Assert.Equal(3, result.Ranked[3].Candidate.Result.TmdbId);
        Assert.True(result.Reordered >= 1);
    }
}
