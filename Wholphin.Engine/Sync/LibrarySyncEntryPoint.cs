using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Wholphin.Engine.Sync;

/// <summary>
/// Hosted service that drives catalog sync: subscribes to Jellyfin library events, funnels them
/// through a single-reader channel (SQLite is single-writer), and runs an initial scan when empty.
/// </summary>
public class LibrarySyncEntryPoint : IHostedService
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILibrarySyncService _sync;
    private readonly ILogger<LibrarySyncEntryPoint> _logger;
    private readonly Channel<SyncMessage> _channel =
        Channel.CreateUnbounded<SyncMessage>(new UnboundedChannelOptions { SingleReader = true });

    private readonly CancellationTokenSource _cts = new();
    private Task? _worker;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibrarySyncEntryPoint"/> class.
    /// </summary>
    public LibrarySyncEntryPoint(
        ILibraryManager libraryManager,
        ILibrarySyncService sync,
        ILogger<LibrarySyncEntryPoint> logger)
    {
        _libraryManager = libraryManager;
        _sync = sync;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded += OnItemAddedOrUpdated;
        _libraryManager.ItemUpdated += OnItemAddedOrUpdated;
        _libraryManager.ItemRemoved += OnItemRemoved;

        _worker = Task.Run(() => ConsumeAsync(_cts.Token), CancellationToken.None);
        _ = Task.Run(() => InitialScanIfEmptyAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemAddedOrUpdated;
        _libraryManager.ItemUpdated -= OnItemAddedOrUpdated;
        _libraryManager.ItemRemoved -= OnItemRemoved;

        _channel.Writer.TryComplete();
        _cts.Cancel();
        return Task.CompletedTask;
    }

    private void OnItemAddedOrUpdated(object? sender, ItemChangeEventArgs e)
    {
        var item = e.Item;
        if (item is null)
        {
            return;
        }

        var kind = item.GetBaseItemKind();
        if (kind != BaseItemKind.Movie && kind != BaseItemKind.Series)
        {
            return; // v1 catalog scope
        }

        _channel.Writer.TryWrite(new SyncMessage(item.Id, Removed: false));
    }

    private void OnItemRemoved(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item is null)
        {
            return;
        }

        _channel.Writer.TryWrite(new SyncMessage(e.Item.Id, Removed: true));
    }

    private async Task ConsumeAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var msg in _channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    if (msg.Removed)
                    {
                        await _sync.RemoveAsync(msg.ItemId, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        await _sync.SyncItemAsync(msg.ItemId, ct).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Orca Engine: failed to sync item {ItemId}.", msg.ItemId);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    private async Task InitialScanIfEmptyAsync(CancellationToken ct)
    {
        try
        {
            if (await _sync.CountAsync(ct).ConfigureAwait(false) == 0)
            {
                _logger.LogInformation("Orca Engine: catalog empty, starting initial scan.");
                await _sync.SyncAllAsync(null, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Orca Engine: initial scan failed.");
        }
    }

    private readonly record struct SyncMessage(Guid ItemId, bool Removed);
}
