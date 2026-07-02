using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Wholphin.Engine.Analytics;

/// <summary>One entry in a popularity leaderboard.</summary>
public record PopularItem(long CatalogId, string Title, int Count);

/// <summary>Popularity leaderboards derived from the behavior log.</summary>
public class PopularityReport
{
    /// <summary>Gets or sets the most-started titles.</summary>
    public IReadOnlyList<PopularItem> MostWatched { get; set; } = new List<PopularItem>();

    /// <summary>Gets or sets the most-completed titles.</summary>
    public IReadOnlyList<PopularItem> MostCompleted { get; set; } = new List<PopularItem>();

    /// <summary>Gets or sets the most-requested titles.</summary>
    public IReadOnlyList<PopularItem> MostRequested { get; set; } = new List<PopularItem>();

    /// <summary>Gets or sets the most-rewatched titles (completed more than once).</summary>
    public IReadOnlyList<PopularItem> MostRewatched { get; set; } = new List<PopularItem>();

    /// <summary>Gets or sets the most-dropped titles (stopped without completing).</summary>
    public IReadOnlyList<PopularItem> MostDropped { get; set; } = new List<PopularItem>();
}

/// <summary>
/// Aggregates the behavior log into popularity leaderboards (Most Watched / Completed / Requested /
/// Rewatched / Dropped). Cached briefly, like the funnel.
/// </summary>
public interface IPopularityService
{
    /// <summary>Builds the popularity report.</summary>
    /// <param name="limit">Max items per leaderboard (1-100).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The report.</returns>
    Task<PopularityReport> BuildAsync(int limit, CancellationToken ct = default);
}
