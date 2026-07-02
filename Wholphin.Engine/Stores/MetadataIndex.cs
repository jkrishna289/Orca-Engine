using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Wholphin.Engine.Data;
using Wholphin.Engine.Data.Entities;

namespace Wholphin.Engine.Stores;

/// <summary>
/// EF Core implementation of <see cref="IMetadataIndex"/>.
/// </summary>
public class MetadataIndex : IMetadataIndex
{
    private readonly IWholphinDbContextFactory _factory;

    /// <summary>
    /// Initializes a new instance of the <see cref="MetadataIndex"/> class.
    /// </summary>
    public MetadataIndex(IWholphinDbContextFactory factory) => _factory = factory;

    /// <inheritdoc />
    public async Task<CatalogItem?> GetByJellyfinIdAsync(Guid jellyfinItemId, CancellationToken ct = default)
    {
        await using var db = _factory.Create();
        return await db.CatalogItems.AsNoTracking()
            .FirstOrDefaultAsync(c => c.JellyfinItemId == jellyfinItemId, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task UpsertAsync(CatalogItem item, CancellationToken ct = default)
    {
        await using var db = _factory.Create();

        CatalogItem? existing = null;
        if (item.JellyfinItemId.HasValue)
        {
            existing = await db.CatalogItems
                .FirstOrDefaultAsync(c => c.JellyfinItemId == item.JellyfinItemId, ct)
                .ConfigureAwait(false);
        }

        if (existing is null && item.TmdbId.HasValue)
        {
            existing = await db.CatalogItems
                .FirstOrDefaultAsync(c => c.TmdbId == item.TmdbId, ct)
                .ConfigureAwait(false);
        }

        if (existing is null)
        {
            db.CatalogItems.Add(item);
        }
        else
        {
            item.Id = existing.Id;
            db.Entry(existing).CurrentValues.SetValues(item);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> CountAsync(CancellationToken ct = default)
    {
        await using var db = _factory.Create();
        return await db.CatalogItems.CountAsync(ct).ConfigureAwait(false);
    }
}
