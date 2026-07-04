using Wholphin.Engine.Ranking;
using Xunit;

namespace Wholphin.Engine.Tests;

/// <summary>Unit tests for the shared scoring vocabulary, calibration, and weight defaults.</summary>
public class ScoringPolicyTests
{
    [Fact]
    public void DefaultRecommenderWeights_ReproduceBlueprintConstants()
    {
        var w = ScoringWeights.DefaultRecommender;
        Assert.Equal(0.60, w.Taste, 6);
        Assert.Equal(0.15, w.Quality, 6);
        Assert.Equal(0.05, w.Freshness, 6);
        Assert.Equal(0.10, w.Availability, 6);
        Assert.Equal(0.0, w.Popularity, 6); // unused by the recommender
    }

    [Fact]
    public void Recommender_Blend_MatchesLegacyFormula()
    {
        // Legacy: 0.60*personalized + 0.15*quality + 0.05*recency + 0.10*availability.
        var signals = new RecommendationSignals
        {
            Taste = 0.8,        // personalized
            Quality = 0.6,
            Freshness = 0.4,    // recency
            Availability = 1.0,
        };
        var expected = (0.60 * 0.8) + (0.15 * 0.6) + (0.05 * 0.4) + (0.10 * 1.0);
        Assert.Equal(expected, signals.Blend(ScoringWeights.DefaultRecommender), 6);
    }

    [Fact]
    public void Blend_AppliesModifiers()
    {
        var signals = new RecommendationSignals
        {
            Taste = 1.0,
            CountryBoost = 0.05,
            InterestMultiplier = 0.5,
            ExposurePenalty = 0.10,
            DiversityPenalty = 0.05,
        };
        // base = 0.55*1 + 0.05 = 0.60; *0.5 = 0.30; -0.10 -0.05 = 0.15.
        Assert.Equal(0.15, signals.Blend(ScoringWeights.DefaultDiscovery), 6);
    }

    [Theory]
    [InlineData(1.0, 0.9, 0.2, 0.9)]  // full confidence → personal
    [InlineData(0.0, 0.9, 0.2, 0.2)]  // no confidence → prior
    [InlineData(0.5, 0.8, 0.4, 0.6)]  // half → midpoint
    public void Calibration_ShrinksTowardPrior(double confidence, double personal, double prior, double expected)
    {
        Assert.Equal(expected, Calibration.Shrink(confidence, personal, prior), 6);
    }

    [Fact]
    public void Calibration_ClampsConfidence()
    {
        Assert.Equal(0.9, Calibration.Shrink(2.0, 0.9, 0.2), 6); // >1 clamps to 1 → personal
        Assert.Equal(0.2, Calibration.Shrink(-1.0, 0.9, 0.2), 6); // <0 clamps to 0 → prior
    }
}
