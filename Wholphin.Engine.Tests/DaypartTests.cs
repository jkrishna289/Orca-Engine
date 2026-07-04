using Wholphin.Engine.Personalization;
using Xunit;

namespace Wholphin.Engine.Tests;

/// <summary>Unit tests for the daypart bucketing boundaries.</summary>
public class DaypartTests
{
    [Theory]
    [InlineData(0, Daypart.Night)]
    [InlineData(4, Daypart.Night)]
    [InlineData(5, Daypart.Morning)]
    [InlineData(11, Daypart.Morning)]
    [InlineData(12, Daypart.Afternoon)]
    [InlineData(16, Daypart.Afternoon)]
    [InlineData(17, Daypart.Evening)]
    [InlineData(21, Daypart.Evening)]
    [InlineData(22, Daypart.Night)]
    [InlineData(23, Daypart.Night)]
    public void Of_BucketsHoursCorrectly(int hour, string expected)
    {
        Assert.Equal(expected, Daypart.Of(hour));
    }
}
