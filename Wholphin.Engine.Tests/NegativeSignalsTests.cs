using Wholphin.Engine.Behavior;
using Xunit;

namespace Wholphin.Engine.Tests;

/// <summary>Unit tests for the derived dis-interest penalties.</summary>
public class NegativeSignalsTests
{
    [Fact]
    public void ShownManyTimesNeverEngaged_Penalized()
    {
        // 5 impressions, no engagement → IgnorePenalty × min(5/5, cap) = 2.0.
        Assert.Equal(2.0, NegativeSignals.Penalty(shown: 5, focused: 0, clicked: 0, played: 0), 6);
    }

    [Fact]
    public void IgnorePenalty_IsCapped()
    {
        // 15 impressions → 2.0 × min(3, 3 cap) = 6.0.
        Assert.Equal(6.0, NegativeSignals.Penalty(shown: 15, focused: 0, clicked: 0, played: 0), 6);
    }

    [Fact]
    public void AnyEngagement_BreaksIgnorePenalty()
    {
        Assert.Equal(0.0, NegativeSignals.Penalty(shown: 10, focused: 1, clicked: 0, played: 0), 6);
    }

    [Fact]
    public void FocusedButNeverPlayed_SoftlyPenalized()
    {
        Assert.Equal(1.0, NegativeSignals.Penalty(shown: 0, focused: 4, clicked: 0, played: 0), 6);
    }

    [Fact]
    public void Played_HasNoPenalty()
    {
        Assert.Equal(0.0, NegativeSignals.Penalty(shown: 10, focused: 5, clicked: 0, played: 1), 6);
    }
}
