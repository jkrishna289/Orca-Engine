using System.Threading;
using System.Threading.Tasks;

namespace Wholphin.Engine.Catalog;

/// <summary>
/// Imports not-yet-available (requestable) titles from the discovery source (Jellyseerr) into the
/// unified catalog, so the engine can surface availability-aware rows like "Worth Requesting".
/// </summary>
public interface IDiscoveryImporter
{
    /// <summary>
    /// Pulls a few pages of movie + series discovery results and adds any that are not already in
    /// the catalog as <see cref="Data.Enums.AvailabilityState.Request"/> items. No-op (returns 0)
    /// when the feature is disabled or Jellyseerr is not configured.
    /// </summary>
    /// <param name="maxPagesPerType">How many discovery pages to pull per media type.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of new catalog rows added.</returns>
    Task<int> ImportAsync(int maxPagesPerType = 1, CancellationToken ct = default);
}
