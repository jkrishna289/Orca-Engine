using System;
using Wholphin.Engine.Data.Enums;
using Wholphin.Engine.Trailer;
using Xunit;

namespace Wholphin.Engine.Tests;

/// <summary>
/// Failed trailers are left alone for a while instead of re-running yt-dlp on every card focus, and the
/// failure reason is what the tool actually printed.
/// </summary>
public class TrailerBackoffTests
{
    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 10)]
    [InlineData(4, 40)]
    [InlineData(9, 1280)]      // 5·2⁸, still under a day
    [InlineData(10, 1440)]     // capped at a day
    [InlineData(540, 1440)]
    public void TemporaryFailuresWaitLongerEachTimeUpToADay(int failures, int minutes) =>
        Assert.Equal(TimeSpan.FromMinutes(minutes), TrailerService.RetryAfter(TrailerState.FailedTemporary, failures));

    [Fact]
    public void NoTrailerWaitsAWeekAndOtherStatesNeverWait()
    {
        Assert.Equal(TimeSpan.FromDays(7), TrailerService.RetryAfter(TrailerState.FailedPermanent, 1));
        Assert.Null(TrailerService.RetryAfter(TrailerState.Expired, 50));
        Assert.Null(TrailerService.RetryAfter(TrailerState.Transcoding, 3));
    }

    [Fact]
    public void FailureReasonIsTheToolsLastLine()
    {
        var stderr = "[youtube] abc: Downloading webpage\r\nWARNING: something\r\nERROR: [youtube] abc: Sign in to confirm you're not a bot\r\n\r\n";
        Assert.Equal("ERROR: [youtube] abc: Sign in to confirm you're not a bot", TrailerService.LastLine(stderr));
        Assert.Null(TrailerService.LastLine("  \n "));
        Assert.Equal(300, TrailerService.LastLine(new string('x', 900))!.Length);
    }
}
