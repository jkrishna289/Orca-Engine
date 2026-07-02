using System;
using System.Threading;
using System.Threading.Tasks;
using Wholphin.Engine.Data.Entities;

namespace Wholphin.Engine.Stores;

/// <summary>
/// Read/write access to the unified catalog (the engine's projection of media).
/// </summary>
public interface IMetadataIndex
{
    /// <summary>Gets a catalog item by its Jellyfin id.</summary>
    Task<CatalogItem?> GetByJellyfinIdAsync(Guid jellyfinItemId, CancellationToken ct = default);

    /// <summary>Inserts or updates a catalog item (matched by Jellyfin id, then TMDB id).</summary>
    Task UpsertAsync(CatalogItem item, CancellationToken ct = default);

    /// <summary>Counts catalog items.</summary>
    Task<int> CountAsync(CancellationToken ct = default);
}
