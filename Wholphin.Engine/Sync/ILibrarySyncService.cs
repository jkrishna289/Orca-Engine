using System;
using System.Threading;
using System.Threading.Tasks;

namespace Wholphin.Engine.Sync;

/// <summary>
/// Keeps the engine catalog in sync with the Jellyfin library.
/// </summary>
public interface ILibrarySyncService
{
    /// <summary>Full/reconciliation scan of Movies + Series; returns the number processed.</summary>
    Task<int> SyncAllAsync(IProgress<double>? progress = null, CancellationToken ct = default);

    /// <summary>Upserts a single item (or removes it if no longer present).</summary>
    Task SyncItemAsync(Guid itemId, CancellationToken ct = default);

    /// <summary>Removes an item from the catalog by Jellyfin id.</summary>
    Task RemoveAsync(Guid itemId, CancellationToken ct = default);

    /// <summary>Counts catalog items.</summary>
    Task<int> CountAsync(CancellationToken ct = default);
}
