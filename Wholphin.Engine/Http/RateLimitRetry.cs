using System;
using System.Net.Http;

namespace Wholphin.Engine.Http;

/// <summary>
/// Shared 429 (rate limit) backoff for outbound API clients (LLM, embeddings). Reads the provider's
/// <c>Retry-After</c> header (delta-seconds or HTTP-date form) and clamps it to a ceiling so one
/// rate-limited call can't stall a discovery/curation cycle. Every caller retries at most once —
/// a burst that survives two attempts is a real outage, not a transient limit, and callers already
/// fail soft (null/empty) beyond that.
/// </summary>
public static class RateLimitRetry
{
    /// <summary>How many attempts a 429-aware caller makes in total (the first try + one retry).</summary>
    public const int MaxAttempts = 2;

    /// <summary>The longest we'll ever wait on a single 429, regardless of what the header asks for.</summary>
    public static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(10);

    /// <summary>Used when the response gives no usable Retry-After hint.</summary>
    public static readonly TimeSpan DefaultDelay = TimeSpan.FromSeconds(2);

    /// <summary>Reads the backoff delay for a 429 response, clamped to <see cref="MaxDelay"/>.</summary>
    /// <param name="response">The rate-limited response.</param>
    /// <returns>How long to wait before retrying once.</returns>
    public static TimeSpan DelayFor(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header is null)
        {
            return DefaultDelay;
        }

        if (header.Delta is { } delta)
        {
            return Clamp(delta);
        }

        if (header.Date is { } date)
        {
            var untilDate = date - DateTimeOffset.UtcNow;
            return untilDate > TimeSpan.Zero ? Clamp(untilDate) : DefaultDelay;
        }

        return DefaultDelay;
    }

    private static TimeSpan Clamp(TimeSpan value)
        => value < TimeSpan.Zero ? DefaultDelay : value > MaxDelay ? MaxDelay : value;
}
