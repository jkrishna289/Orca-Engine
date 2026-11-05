using Microsoft.Data.Sqlite;
using Wholphin.Engine.Contracts;
using Wholphin.Engine.Data.Entities;
using Wholphin.Engine.Data.Enums;
using Wholphin.Engine.Home;
using Xunit;

namespace Wholphin.Engine.Tests;

/// <summary>
/// A card for a title the library has must carry its library id. Without one, Info and Play open the
/// not-in-your-library page for a show that's right there.
/// </summary>
public sealed class HomeLibraryIdTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly TestDbFactory _factory;

    public HomeLibraryIdTests()
    {
        _connection.Open();
        _factory = new TestDbFactory(_connection);
        using var db = _factory.Create();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private static RenderItem Card(int tmdbId, MediaType type, Guid? jellyfinId = null) =>
        new() { Media = new MediaIdDto { TmdbId = tmdbId, MediaType = type, JellyfinId = jellyfinId, Source = jellyfinId is null ? MediaSource.Tmdb : MediaSource.Jellyfin } };

    [Fact]
    public async Task CardsGetTheLibraryIdOfTheSameTitle()
    {
        var kevin = Guid.NewGuid();
        var other = Guid.NewGuid();
        await using (var db = _factory.Create())
        {
            db.CatalogItems.AddRange(
                new CatalogItem { TmdbId = 99652, MediaType = MediaType.Series, Title = "Kevin Can F**K Himself", JellyfinItemId = kevin, Availability = AvailabilityState.WatchNow },
                new CatalogItem { TmdbId = 99652, MediaType = MediaType.Series, Title = "Kevin Can F**K Himself", Availability = AvailabilityState.RecentlyAdded },
                // Same TMDB number, a movie: TMDB reuses ids across movies and series
                new CatalogItem { TmdbId = 1434, MediaType = MediaType.Movie, Title = "A film", JellyfinItemId = other, Availability = AvailabilityState.WatchNow });
            await db.SaveChangesAsync();
        }

        var bundle = new RenderBundle
        {
            Rows =
            [
                new RenderRow { Id = "foryou", Items = [Card(99652, MediaType.Series), Card(1434, MediaType.Series), Card(456, MediaType.Series)] },
                new RenderRow { Id = "continue", Items = [Card(99652, MediaType.Series, kevin)] },
            ],
        };

        await HomeService.AttachLibraryIdsAsync(bundle, _factory, CancellationToken.None);

        var row = bundle.Rows[0].Items;
        Assert.Equal(kevin, row[0].Media.JellyfinId);
        Assert.Equal(MediaSource.Jellyfin, row[0].Media.Source);
        Assert.Null(row[1].Media.JellyfinId);   // only a movie has that number
        Assert.Null(row[2].Media.JellyfinId);   // not in the library: stays the not-in-your-library card
    }
}
