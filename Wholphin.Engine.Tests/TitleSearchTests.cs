using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Wholphin.Engine.Catalog;
using Wholphin.Engine.Data.Entities;
using Wholphin.Engine.Data.Enums;
using Wholphin.Engine.Integrations.Jellyseerr;
using Wholphin.Engine.Integrations.Tmdb;
using Xunit;

namespace Wholphin.Engine.Tests;

/// <summary>
/// Searching for a title that isn't on the server: TMDB's mixed results become titles, and each one
/// gets a catalog row so its page can show details and More Like This.
/// </summary>
public class TitleSearchTests : IDisposable
{
    private static readonly Dictionary<int, string> NoGenres = new();

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly TestDbFactory _factory;

    public TitleSearchTests()
    {
        _connection.Open();
        _factory = new TestDbFactory(_connection);
        using var db = _factory.Create();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private static TmdbDiscoverItem Hit(int id, string type, string name) =>
        new() { Id = id, MediaType = type, Title = type == "movie" ? name : null, Name = type == "movie" ? null : name, PosterPath = "/p.jpg" };

    private static DiscoverResult Title(int id, MediaType type, string title) =>
        new() { TmdbId = id, MediaType = type, Title = title, Availability = AvailabilityState.Request };

    [Fact]
    public void MultiSearchKeepsMoviesAndSeriesInOrderAndDropsPeople()
    {
        var page = new List<TmdbDiscoverItem>
        {
            Hit(1, "tv", "Dune: Prophecy"),
            Hit(2, "person", "Denis Villeneuve"),
            Hit(3, "movie", "Dune"),
            new() { Id = 4, MediaType = "movie", Title = "No Art" }, // no poster or backdrop
        };

        var titles = TmdbClient.MapMultiSearch(page, NoGenres, NoGenres);

        Assert.Equal(new[] { "Dune: Prophecy", "Dune" }, titles.Select(t => t.Title));
        Assert.Equal(new[] { MediaType.Series, MediaType.Movie }, titles.Select(t => t.MediaType));
    }

    [Fact]
    public async Task ResultsReuseExistingRowsPreferTheLibraryCopyAndImportTheRest()
    {
        using (var db = _factory.Create())
        {
            db.CatalogItems.AddRange(
                new CatalogItem { TmdbId = 10, MediaType = MediaType.Movie, Title = "Dune", Availability = AvailabilityState.Request },
                new CatalogItem { TmdbId = 10, MediaType = MediaType.Movie, Title = "Dune", JellyfinItemId = Guid.NewGuid(), Availability = AvailabilityState.WatchNow });
            db.SaveChanges();
        }

        Dictionary<(int TmdbId, MediaType Type), CatalogItem> rows;
        using (var db = _factory.Create())
        {
            rows = await TmdbTitleRows.MatchOrImportAsync(
                db,
                new[] { Title(10, MediaType.Movie, "Dune"), Title(11, MediaType.Series, "Dune: Prophecy"), Title(12, MediaType.Movie, "Dune Part Two") },
                maxImports: 1,
                CancellationToken.None);
        }

        Assert.NotNull(rows[(10, MediaType.Movie)].JellyfinItemId);    // the library copy, not the TMDB one
        Assert.True(rows[(11, MediaType.Series)].Id > 0);               // imported and saved
        Assert.False(rows.ContainsKey((12, MediaType.Movie)));          // over the import cap

        using var check = _factory.Create();
        var imported = check.CatalogItems.Single(c => c.TmdbId == 11);
        Assert.Equal(AvailabilityState.Request, imported.Availability);
        Assert.True(imported.LastSyncedAt > DateTime.UtcNow.AddMinutes(-1)); // inside the sweep's grace period
    }

    [Fact]
    public async Task ARowOutsideTheLibraryTakesTmdbsTitleAndALibraryRowKeepsItsOwn()
    {
        using (var db = _factory.Create())
        {
            db.CatalogItems.AddRange(
                new CatalogItem { TmdbId = 20, MediaType = MediaType.Movie, Title = "recon-test", Availability = AvailabilityState.Request },
                new CatalogItem { TmdbId = 21, MediaType = MediaType.Movie, Title = "Dune (2021)", JellyfinItemId = Guid.NewGuid(), Availability = AvailabilityState.WatchNow });
            db.SaveChanges();
        }

        using (var db = _factory.Create())
        {
            await TmdbTitleRows.MatchOrImportAsync(
                db,
                new[] { Title(20, MediaType.Movie, "Interstellar"), Title(21, MediaType.Movie, "Dune") },
                maxImports: 0,
                CancellationToken.None);
        }

        using var check = _factory.Create();
        Assert.Equal("Interstellar", check.CatalogItems.Single(c => c.TmdbId == 20).Title);
        Assert.Equal("Dune (2021)", check.CatalogItems.Single(c => c.TmdbId == 21).Title);
    }
}
