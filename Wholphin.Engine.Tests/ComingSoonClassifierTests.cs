using Wholphin.Engine.Catalog;
using Xunit;

namespace Wholphin.Engine.Tests;

/// <summary>Unit tests for the "Coming Soon (For You)" inclusion / sub-type rules.</summary>
public class ComingSoonClassifierTests
{
    [Fact]
    public void MonitoredSeries_IsNextEpisode()
    {
        var kind = ComingSoonClassifier.Classify(
            isSeries: true, isMonitored: true, userWatchedPrior: false, followsFranchise: false,
            intentScore: 0, confidence: 0, communityRating: null);

        Assert.Equal(ComingSoonKind.NextEpisode, kind);
    }

    [Fact]
    public void MonitoredMovie_IsTrending()
    {
        var kind = ComingSoonClassifier.Classify(
            isSeries: false, isMonitored: true, userWatchedPrior: false, followsFranchise: false,
            intentScore: 0, confidence: 0, communityRating: null);

        Assert.Equal(ComingSoonKind.Trending, kind);
    }

    [Fact]
    public void MonitoredBeatsActiveDislike()
    {
        // Explicit interest must bypass the not-interested gate.
        var kind = ComingSoonClassifier.Classify(
            isSeries: true, isMonitored: true, userWatchedPrior: false, followsFranchise: false,
            intentScore: -5.0, confidence: 0.9, communityRating: 2.0);

        Assert.Equal(ComingSoonKind.NextEpisode, kind);
    }

    [Fact]
    public void WatchedPriorSeason_IsNewSeason()
    {
        var kind = ComingSoonClassifier.Classify(
            isSeries: true, isMonitored: false, userWatchedPrior: true, followsFranchise: false,
            intentScore: 0, confidence: 0.9, communityRating: null);

        Assert.Equal(ComingSoonKind.NewSeason, kind);
    }

    [Fact]
    public void FollowsFranchise_IsNewSeason()
    {
        var kind = ComingSoonClassifier.Classify(
            isSeries: true, isMonitored: false, userWatchedPrior: false, followsFranchise: true,
            intentScore: 0, confidence: 0.9, communityRating: null);

        Assert.Equal(ComingSoonKind.NewSeason, kind);
    }

    [Fact]
    public void ColdProfile_HighRated_IsTrending()
    {
        // Cold (unjudgeable) profiles fall back to global quality.
        var kind = ComingSoonClassifier.Classify(
            isSeries: true, isMonitored: false, userWatchedPrior: false, followsFranchise: false,
            intentScore: 0, confidence: 0.0, communityRating: 8.1);

        Assert.Equal(ComingSoonKind.Trending, kind);
    }

    [Fact]
    public void WarmProfile_TasteAligned_HighRated_IsTrending()
    {
        var kind = ComingSoonClassifier.Classify(
            isSeries: true, isMonitored: false, userWatchedPrior: false, followsFranchise: false,
            intentScore: 1.2, confidence: 0.8, communityRating: 7.5);

        Assert.Equal(ComingSoonKind.Trending, kind);
    }

    [Fact]
    public void WarmProfile_NotTasteAligned_IsExcluded()
    {
        // Warm profile, neutral taste (not > threshold) → we can judge it and it doesn't fit.
        var kind = ComingSoonClassifier.Classify(
            isSeries: true, isMonitored: false, userWatchedPrior: false, followsFranchise: false,
            intentScore: 0, confidence: 0.8, communityRating: 8.5);

        Assert.Equal(ComingSoonKind.Excluded, kind);
    }

    [Fact]
    public void LowRated_IsExcluded()
    {
        var kind = ComingSoonClassifier.Classify(
            isSeries: false, isMonitored: false, userWatchedPrior: false, followsFranchise: false,
            intentScore: 1.0, confidence: 0.8, communityRating: 4.0);

        Assert.Equal(ComingSoonKind.Excluded, kind);
    }

    [Fact]
    public void ActivelyDisliked_IsExcluded()
    {
        var kind = ComingSoonClassifier.Classify(
            isSeries: false, isMonitored: false, userWatchedPrior: false, followsFranchise: false,
            intentScore: -1.0, confidence: 0.8, communityRating: 9.0);

        Assert.Equal(ComingSoonKind.Excluded, kind);
    }
}
