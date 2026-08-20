using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Wholphin.Engine.Http;

/// <summary>
/// The single choke point every external metadata call passes through: circuit breaker, concurrency
/// cap, throttle, timeout, and health accounting. Keeps one broken provider from slowing every
/// metadata request down to its timeout.
/// </summary>
public interface IProviderGate
{
    /// <summary>
    /// Runs one provider call under the breaker, concurrency gate, throttle and timeout.
    /// </summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="provider">The provider's name (its priority token).</param>
    /// <param name="operation">The call to make.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The operation's result, or null when it failed, timed out, or the breaker was open.</returns>
    /// <remarks>
    /// Never throws except when <paramref name="cancellationToken"/> itself is cancelled — that is
    /// the caller going away, not a provider fault, and callers need to see it.
    /// </remarks>
    Task<T?> ExecuteAsync<T>(string provider, System.Func<CancellationToken, Task<T?>> operation, CancellationToken cancellationToken)
        where T : class;

    /// <summary>Returns a point-in-time health snapshot for every provider seen so far.</summary>
    /// <param name="configured">Names currently holding an API key, so the view can distinguish "off" from "broken".</param>
    /// <returns>The snapshot, ordered by name.</returns>
    IReadOnlyList<ProviderHealth> Snapshot(IReadOnlyCollection<string> configured);
}
