using System;
using System.Net;
using System.Net.Http;
using Wholphin.Engine.Http;
using Xunit;

namespace Wholphin.Engine.Tests;

public class RateLimitRetryTests
{
    [Fact]
    public void NoRetryAfterHeader_UsesDefaultDelay()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        Assert.Equal(RateLimitRetry.DefaultDelay, RateLimitRetry.DelayFor(response));
    }

    [Fact]
    public void DeltaSecondsHeader_IsHonored()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(4));

        Assert.Equal(TimeSpan.FromSeconds(4), RateLimitRetry.DelayFor(response));
    }

    [Fact]
    public void DeltaSecondsHeader_ClampsToMaxDelay()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(5));

        Assert.Equal(RateLimitRetry.MaxDelay, RateLimitRetry.DelayFor(response));
    }

    [Fact]
    public void DateHeaderInFuture_ComputesRemainingDelay()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddSeconds(3));

        var delay = RateLimitRetry.DelayFor(response);
        Assert.True(delay > TimeSpan.Zero && delay <= TimeSpan.FromSeconds(3.5));
    }

    [Fact]
    public void DateHeaderInPast_FallsBackToDefault()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddSeconds(-30));

        Assert.Equal(RateLimitRetry.DefaultDelay, RateLimitRetry.DelayFor(response));
    }
}
