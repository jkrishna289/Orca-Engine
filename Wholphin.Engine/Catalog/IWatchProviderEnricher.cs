using System.Threading;
using System.Threading.Tasks;

namespace Wholphin.Engine.Catalog;

/// <summary>
/// Tags catalog rows with their primary streaming-provider brand (Netflix/Prime/Disney+/…) from TMDB
/// watch providers, and caches the provider logos — powering the studio/provider card tag. Covers both
/// library and requestable items (keyed on TMDB id). Gated on a TMDB key; fail-soft.
/// </summary>
public interface IWatchProviderEnricher
{
    /// <summary>
    /// Resolves the watch-provider brand for a batch of not-yet-tagged rows (round-robin, oldest-first).
    /// </summary>
    /// <param name="maxItems">Maximum rows to tag this pass.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of rows tagged with a provider brand.</returns>
    Task<int> EnrichAsync(int maxItems = 40, CancellationToken ct = default);
}
