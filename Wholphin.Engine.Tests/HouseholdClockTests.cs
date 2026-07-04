using System;
using Wholphin.Engine.Personalization;
using Xunit;

namespace Wholphin.Engine.Tests;

/// <summary>Unit tests for the household clock (time-zone resolution for daypart bucketing).</summary>
public class HouseholdClockTests
{
    [Fact]
    public void Resolve_WithNoPluginConfig_FallsBackToServerLocal()
    {
        // Plugin.Instance is null in the test host → server local zone, never throws.
        Assert.Equal(TimeZoneInfo.Local, HouseholdClock.Resolve());
    }

    [Fact]
    public void Hour_IsInRange()
    {
        var hour = HouseholdClock.Hour();
        Assert.InRange(hour, 0, 23);
    }

    [Fact]
    public void Hour_MatchesResolvedZone()
    {
        var expected = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, HouseholdClock.Resolve()).Hour;
        Assert.Equal(expected, HouseholdClock.Hour());
    }
}
