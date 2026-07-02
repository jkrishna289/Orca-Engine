using System.Threading;
using System.Threading.Tasks;

namespace Wholphin.Engine.Catalog;

/// <summary>
/// Backfills metadata (genre names + artwork + trailer) onto requestable/discover catalog rows that
/// are missing it, using TMDB. Library-synced rows are left untouched (they carry Jellyfin metadata
/// and resolve art from the Jellyfin id). No-op when TMDB is unconfigured.
/// </summary>
public interface ICatalogEnricher
{
    /// <summary>
    /// Enriches up to <paramref name="maxItems"/> requestable rows that lack genres or artwork,
    /// oldest-touched first (round-robin).
    /// </summary>
    /// <param name="maxItems">Maximum rows to process this pass (1-500).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of rows actually enriched.</returns>
    Task<int> EnrichAsync(int maxItems = 50, CancellationToken ct = default);
}
