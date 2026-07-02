using System.Threading;
using System.Threading.Tasks;

namespace Wholphin.Engine.Metadata;

/// <summary>
/// Pre-generates content advisories for catalog titles (movies + series) in the background via Groq, so
/// they're already stored by the time a viewer hits play. Gated on a Groq key + the feature flag; fail-soft.
/// </summary>
public interface IContentWarningEnricher
{
    /// <summary>
    /// Generates + stores advisories for a batch of not-yet-attempted titles (round-robin, oldest-first).
    /// </summary>
    /// <param name="maxItems">Maximum titles to process this pass.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of titles processed.</returns>
    Task<int> EnrichAsync(int maxItems = 25, CancellationToken ct = default);
}
