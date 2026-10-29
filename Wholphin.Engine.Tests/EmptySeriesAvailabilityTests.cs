using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Wholphin.Engine.Data.Entities;
using Wholphin.Engine.Data.Enums;
using Wholphin.Engine.Playback;
using Wholphin.Engine.Stores;
using Wholphin.Engine.Sync;
using Xunit;

namespace Wholphin.Engine.Tests;

/// <summary>
/// A series Sonarr set up before anything downloaded is in the library with nothing to play. As
/// WatchNow it was recommended, billboard included, with a Play button that opened an empty page.
/// </summary>
public sealed class EmptySeriesAvailabilityTests
{
    [Theory]
    [InlineData(0, AvailabilityState.Unavailable)]
    [InlineData(3, AvailabilityState.WatchNow)]
    public async Task SeriesIsPlayableOnlyOnceItHasAnEpisode(int episodes, AvailabilityState expected)
    {
        var series = new Series { Id = Guid.NewGuid(), Name = "The Family Man" };
        InternalItemsQuery? counted = null;
        CatalogItem? upserted = null;
        var library = JellyfinProgressBridgeTests.ForwardingProxy.Create<ILibraryManager>((method, args) =>
        {
            switch (method.Name)
            {
                case "GetItemById": return series;
                case "GetPeople": return new List<MediaBrowser.Controller.Entities.PersonInfo>();
                case "GetCount": counted = (InternalItemsQuery)args![0]!; return episodes;
                default: throw new NotSupportedException(method.Name);
            }
        });
        var index = JellyfinProgressBridgeTests.ForwardingProxy.Create<IMetadataIndex>((_, args) =>
        {
            upserted = (CatalogItem)args![0]!;
            return Task.CompletedTask;
        });
        var bridge = JellyfinProgressBridgeTests.ForwardingProxy.Create<IJellyfinProgressBridge>((_, _) => Task.CompletedTask);
        var sync = new LibrarySyncService(library, null!, index, NullLogger<LibrarySyncService>.Instance, bridge);

        await sync.SyncItemAsync(series.Id);

        Assert.Equal(expected, upserted!.Availability);
        Assert.Equal([series.Id], counted!.AncestorIds);
        Assert.Equal([BaseItemKind.Episode], counted.IncludeItemTypes);
        Assert.False(counted.IsVirtualItem); // a missing-episode placeholder has nothing to play either
    }

    [Fact]
    public async Task EpisodeArrivingResyncsItsSeries()
    {
        EventHandler<ItemChangeEventArgs>? added = null;
        var library = JellyfinProgressBridgeTests.ForwardingProxy.Create<ILibraryManager>((method, args) =>
        {
            if (method.Name == "add_ItemAdded")
            {
                added += (EventHandler<ItemChangeEventArgs>)args![0]!;
            }

            return null;
        });
        var synced = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sync = JellyfinProgressBridgeTests.ForwardingProxy.Create<ILibrarySyncService>((method, args) =>
        {
            switch (method.Name)
            {
                case "CountAsync": return Task.FromResult(1);
                case "SyncItemAsync": synced.TrySetResult((Guid)args![0]!); return Task.CompletedTask;
                default: throw new NotSupportedException(method.Name);
            }
        });
        var entry = new LibrarySyncEntryPoint(library, sync, NullLogger<LibrarySyncEntryPoint>.Instance);
        await entry.StartAsync(default);

        var seriesId = Guid.NewGuid();
        added!.Invoke(null, new ItemChangeEventArgs { Item = new Episode { Id = Guid.NewGuid(), SeriesId = seriesId } });

        Assert.Equal(seriesId, await synced.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        await entry.StopAsync(default);
    }
}
