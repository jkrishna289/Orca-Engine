using System;

namespace Wholphin.Engine.Http;

/// <summary>
/// A point-in-time view of one provider's health, for the diagnostics endpoint.
/// </summary>
/// <remarks>
/// Deliberately carries no key, URL, or exception message — only a failure TYPE name. Provider URLs
/// embed API keys in their query strings, and this object is serialized straight to an admin endpoint.
/// </remarks>
public sealed class ProviderHealth
{
    /// <summary>Gets the provider's priority token ("tmdb", "omdb", …).</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Gets a value indicating whether an API key is configured. Never the key itself, not even masked.</summary>
    public bool Configured { get; init; }

    /// <summary>Gets the number of calls that returned data.</summary>
    public long Success { get; init; }

    /// <summary>Gets the number of calls that succeeded but had nothing for the title. Not a failure.</summary>
    public long Empty { get; init; }

    /// <summary>Gets the number of failed calls.</summary>
    public long Failure { get; init; }

    /// <summary>Gets the number of calls that timed out.</summary>
    public long Timeout { get; init; }

    /// <summary>Gets the number of calls rejected with 429. Not counted as a failure.</summary>
    public long RateLimited { get; init; }

    /// <summary>Gets the number of calls refused without dialing because the breaker was open.</summary>
    public long ShortCircuited { get; init; }

    /// <summary>Gets the current consecutive-failure streak (resets on any success).</summary>
    public int ConsecutiveFailures { get; init; }

    /// <summary>Gets the exponentially-weighted mean latency in milliseconds.</summary>
    public double AvgLatencyMs { get; init; }

    /// <summary>Gets when the provider last returned data.</summary>
    public DateTime? LastSuccessUtc { get; init; }

    /// <summary>Gets when the provider last failed.</summary>
    public DateTime? LastFailureUtc { get; init; }

    /// <summary>Gets the exception TYPE name of the last failure (never its message).</summary>
    public string? LastFailureKind { get; init; }

    /// <summary>Gets a value indicating whether the circuit breaker is currently open.</summary>
    public bool BreakerOpen { get; init; }

    /// <summary>Gets when the breaker next admits a probe.</summary>
    public DateTime? BreakerOpenUntilUtc { get; init; }
}
