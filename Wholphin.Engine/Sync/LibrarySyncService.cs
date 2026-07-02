using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wholphin.Engine.Data;
using Wholphin.Engine.Data.Entities;
using Wholphin.Engine.Stores;

namespace Wholphin.Engine.Sync;

/// <summary>
/// Projects the Jellyfin library (Movies + Series in v1) into the engine catalog.
/// </summary>
public class LibrarySyncService : ILibrarySyncService
{
    private const string LibraryCursorId = "library";
    private const int BatchSize = 200;

    private static readonly BaseItemKind[] SyncedKinds = { BaseItemKind.Movie, BaseItemKind.Series };

    private readonly ILibraryManager _libraryManager;
    private readonly IWholphinDbContextFactory _factory;
    private readonly IMetadataIndex _metadataIndex;
    private readonly ILogger<LibrarySyncService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibrarySyncService"/> class.
    /// </summary>
    public LibrarySyncService(
        ILibraryManager libraryManager,
        IWholphinDbContextFactory factory,
        IMetadataIndex metadataIndex,
        ILogger<LibrarySyncService> logger)
    {
        _libraryManager = libraryManager;
        _factory = factory;
        _metadataIndex = metadataIndex;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<int> SyncAllAsync(IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = SyncedKinds,
            Recursive = true,
            IsVirtualItem = false,
            DtoOptions = new DtoOptions(false),
        };

        var items = _libraryManager.GetItemList(query);
        _logger.LogInformation("Orca Engine: syncing {Count} library items.", items.Count);

        await using var db = _factory.Create();
        var existing = await db.CatalogItems
            .Where(c => c.JellyfinItemId != null)
            .ToDictionaryAsync(c => c.JellyfinItemId!.Value, c => c, ct)
            .ConfigureAwait(false);

        var processed = 0;
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();

            var mapped = CatalogMapper.Map(item, _libraryManager.GetPeople(item));
            if (mapped.JellyfinItemId is { } jid && existing.TryGetValue(jid, out var current))
            {
                mapped.Id = current.Id;
                db.Entry(current).CurrentValues.SetValues(mapped);
            }
            else
            {
                db.CatalogItems.Add(mapped);
            }

            if (++processed % BatchSize == 0)
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                progress?.Report(processed * 100.0 / Math.Max(1, items.Count));
            }
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await UpsertCursorAsync(db, ct).ConfigureAwait(false);
        progress?.Report(100);

        _logger.LogInformation("Orca Engine: catalog sync complete ({Count} items).", processed);
        return processed;
    }

    /// <inheritdoc />
    public async Task SyncItemAsync(Guid itemId, CancellationToken ct = default)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            await RemoveAsync(itemId, ct).ConfigureAwait(false);
            return;
        }

        var mapped = CatalogMapper.Map(item, _libraryManager.GetPeople(item));
        await _metadataIndex.UpsertAsync(mapped, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RemoveAsync(Guid itemId, CancellationToken ct = default)
    {
        await using var db = _factory.Create();
        await db.CatalogItems
            .Where(c => c.JellyfinItemId == itemId)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<int> CountAsync(CancellationToken ct = default) => _metadataIndex.CountAsync(ct);

    private static async Task UpsertCursorAsync(WholphinDbContext db, CancellationToken ct)
    {
        var cursor = await db.SyncStates
            .FirstOrDefaultAsync(s => s.Id == LibraryCursorId, ct)
            .ConfigureAwait(false);

        if (cursor is null)
        {
            db.SyncStates.Add(new SyncState
            {
                Id = LibraryCursorId,
                LastCursorTicks = DateTime.UtcNow.Ticks,
                UpdatedAt = DateTime.UtcNow,
            });
        }
        else
        {
            cursor.LastCursorTicks = DateTime.UtcNow.Ticks;
            cursor.UpdatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
