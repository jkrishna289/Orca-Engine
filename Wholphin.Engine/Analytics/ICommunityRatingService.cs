using System.Threading;
using System.Threading.Tasks;

namespace Wholphin.Engine.Analytics;

/// <summary>
/// Aggregates local behavior signals into a per-item Wholphin community rating (stored on the catalog
/// row) so the app can show "TMDB x.x · Wholphin y.y".
/// </summary>
public interface ICommunityRatingService
{
    /// <summary>
    /// Recomputes the Wholphin rating for every catalog item that has rating signals and persists it.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of items whose rating was written.</returns>
    Task<int> RecomputeAllAsync(CancellationToken ct = default);
}
