using Wholphin.Engine.Discovery;
using Wholphin.Engine.Ranking;
using Xunit;

namespace Wholphin.Engine.Tests;

/// <summary>Unit tests for the deterministic discovery-score blend.</summary>
public class CandidateScorerTests
{
    [Fact]
    public void Taste_DominatesTheBlend()
    {
        var score = new DiscoveryScore { Taste = 1.0 };
        Assert.Equal(0.55, CandidateScorer.ComputeFinal(score, ScoringWeights.DefaultDiscovery), 6);
    }

    [Fact]
    public void CountryBoost_AddsOnTop()
    {
        var score = new DiscoveryScore { Taste = 1.0, CountryBoost = 0.05 };
        Assert.Equal(0.60, CandidateScorer.ComputeFinal(score, ScoringWeights.DefaultDiscovery), 6);
    }

    [Fact]
    public void ExposureAndDiversity_SubtractAfterInterest()
    {
        var score = new DiscoveryScore { Taste = 1.0, ExposurePenalty = 0.10, DiversityPenalty = 0.15 };
        // 0.55 * 1.0 - 0.10 - 0.15 = 0.30.
        Assert.Equal(0.30, CandidateScorer.ComputeFinal(score, ScoringWeights.DefaultDiscovery), 6);
    }

    [Fact]
    public void InterestMultiplier_ScalesTheBaseline()
    {
        var score = new DiscoveryScore { Taste = 1.0, InterestMultiplier = 0.5 };
        Assert.Equal(0.275, CandidateScorer.ComputeFinal(score, ScoringWeights.DefaultDiscovery), 6);
    }

    [Fact]
    public void AllComponents_Blend()
    {
        var score = new DiscoveryScore
        {
            Taste = 1.0,       // 0.55
            Popularity = 1.0,  // 0.20
            Freshness = 1.0,   // 0.10
            Novelty = 1.0,     // 0.10
            SourceConfidence = 1.0, // 0.05
        };
        Assert.Equal(1.0, CandidateScorer.ComputeFinal(score, ScoringWeights.DefaultDiscovery), 6);
    }
}
