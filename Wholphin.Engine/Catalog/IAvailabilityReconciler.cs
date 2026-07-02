using System.Threading;
using System.Threading.Tasks;

namespace Wholphin.Engine.Catalog;

/// <summary>
/// Advances the availability state of in-flight catalog items (Requested/Downloading) by polling
/// Jellyseerr, so a title's state follows its real lifecycle without manual intervention.
/// </summary>
public interface IAvailabilityReconciler
{
    /// <summary>
    /// Reconciles up to <paramref name="maxItems"/> in-flight items against Jellyseerr. No-op
    /// (returns 0) when Jellyseerr is not configured.
    /// </summary>
    /// <param name="maxItems">Maximum items to poll this pass (round-robined by last-synced).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of items whose availability changed.</returns>
    Task<int> ReconcileAsync(int maxItems = 200, CancellationToken ct = default);
}
