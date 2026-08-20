using System;

namespace Wholphin.Engine.Http;

/// <summary>
/// Raised by a provider when the upstream answered 429. Distinct from a failure: being throttled says
/// the provider is healthy and we asked too fast, so it must NOT count toward the circuit breaker.
/// </summary>
public class ProviderRateLimitedException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="ProviderRateLimitedException"/> class.</summary>
    public ProviderRateLimitedException()
        : this(TimeSpan.Zero)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ProviderRateLimitedException"/> class.</summary>
    /// <param name="message">The message.</param>
    public ProviderRateLimitedException(string message)
        : base(message) => RetryAfter = TimeSpan.Zero;

    /// <summary>Initializes a new instance of the <see cref="ProviderRateLimitedException"/> class.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The inner exception.</param>
    public ProviderRateLimitedException(string message, Exception innerException)
        : base(message, innerException) => RetryAfter = TimeSpan.Zero;

    /// <summary>Initializes a new instance of the <see cref="ProviderRateLimitedException"/> class.</summary>
    /// <param name="retryAfter">How long the provider asked us to wait.</param>
    public ProviderRateLimitedException(TimeSpan retryAfter)
        : base("The metadata provider rate-limited the request.") => RetryAfter = retryAfter;

    /// <summary>Gets how long the provider asked us to wait before retrying.</summary>
    public TimeSpan RetryAfter { get; }
}
