using Wholphin.Engine.Behavior;
using Wholphin.Engine.Data.Enums;
using Xunit;

namespace Wholphin.Engine.Tests;

/// <summary>Unit tests for the per-signal affinity weights.</summary>
public class SignalWeightsTests
{
    [Fact]
    public void ThumbsAreStrongestSignals()
    {
        Assert.Equal(12.0, SignalWeights.For(BehaviorEventType.ThumbsUp, 1));
        Assert.Equal(-12.0, SignalWeights.For(BehaviorEventType.ThumbsDown, -1));
    }

    [Theory]
    [InlineData(10.0, 10.0)] // 10/10 ≈ a favorite
    [InlineData(5.0, 0.0)]   // neutral
    [InlineData(0.0, -10.0)] // hated
    public void RatingIsCentredOnFive(double rating, double expected)
    {
        Assert.Equal(expected, SignalWeights.For(BehaviorEventType.Rated, rating), 6);
    }

    [Fact]
    public void PlaybackStopped_EarlyAbandonIsNegative()
    {
        Assert.Equal(-3.0, SignalWeights.For(BehaviorEventType.PlaybackStopped, 0.10));
    }

    [Fact]
    public void PlaybackStopped_NearCompleteIsPositive()
    {
        Assert.Equal(4.0, SignalWeights.For(BehaviorEventType.PlaybackStopped, 0.95));
    }

    [Fact]
    public void TrailerPlayed_IsMildPositive()
    {
        Assert.Equal(0.5, SignalWeights.For(BehaviorEventType.TrailerPlayed, 0));
    }

    [Fact]
    public void CardImpression_CarriesNoPositiveWeight()
    {
        // Volume of "shown" cards must never swamp the profile.
        Assert.Equal(0.0, SignalWeights.For(BehaviorEventType.CardImpression, 0));
    }

    [Fact]
    public void CardFocused_ClampsDwell()
    {
        Assert.Equal(0.0, SignalWeights.For(BehaviorEventType.CardFocused, 0));
        Assert.Equal(0.4, SignalWeights.For(BehaviorEventType.CardFocused, 100)); // clamped
    }
}
