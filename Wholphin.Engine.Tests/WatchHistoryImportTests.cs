using MediaBrowser.Controller.Entities;
using Wholphin.Engine.Behavior;
using Wholphin.Engine.Data.Entities;
using Wholphin.Engine.Data.Enums;
using Xunit;

namespace Wholphin.Engine.Tests;

/// <summary>
/// The history import's event synthesis. This is the load-bearing part: it decides how much of a
/// user's past counts, and it runs once against real accounts, so getting the weighting wrong is
/// not something a later run corrects.
/// </summary>
public class WatchHistoryImportTests
{
    private static readonly Guid User = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Item = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static double Weight(IEnumerable<BehaviorEvent> events) =>
        events.Sum(e => SignalWeights.For(e.EventType, e.Value));

    [Fact]
    public void IgnoresItemsJellyfinKnowsNothingAbout()
    {
        Assert.False(WatchHistoryImporter.HasSignal(new UserItemData { Key = "k" }));
        Assert.True(WatchHistoryImporter.HasSignal(new UserItemData { Key = "k", Played = true }));
        Assert.True(WatchHistoryImporter.HasSignal(new UserItemData { Key = "k", IsFavorite = true }));
        Assert.True(WatchHistoryImporter.HasSignal(new UserItemData { Key = "k", Rating = 9 }));
        Assert.True(WatchHistoryImporter.HasSignal(new UserItemData { Key = "k", PlayCount = 1 }));
    }

    [Fact]
    public void RewatchingCountsForMoreButIsCapped()
    {
        var once = new List<BehaviorEvent>();
        var many = new List<BehaviorEvent>();
        var at = DateTime.UtcNow.AddDays(-10);

        WatchHistoryImporter.AppendMovie(once, User, Item, new UserItemData { Key = "k", Played = true, PlayCount = 1, LastPlayedDate = at });
        WatchHistoryImporter.AppendMovie(many, User, Item, new UserItemData { Key = "k", Played = true, PlayCount = 40, LastPlayedDate = at });

        Assert.True(Weight(many) > Weight(once));

        // A title watched forty times must not be forty times a title watched once, or one comfort
        // rewatch would drown every other thing the user likes.
        Assert.Equal(3, many.Count);
    }

    [Fact]
    public void MarkedPlayedWithNoDateIsTheWeakerSignal()
    {
        var observed = new List<BehaviorEvent>();
        var marked = new List<BehaviorEvent>();

        WatchHistoryImporter.AppendMovie(observed, User, Item, new UserItemData { Key = "k", Played = true, LastPlayedDate = DateTime.UtcNow });
        WatchHistoryImporter.AppendMovie(marked, User, Item, new UserItemData { Key = "k", Played = true, LastPlayedDate = null });

        Assert.Equal(BehaviorEventType.PlaybackCompleted, observed[0].EventType);
        Assert.Equal(BehaviorEventType.MarkedPlayed, marked[0].EventType);
        Assert.True(Weight(marked) < Weight(observed));
    }

    [Fact]
    public void CarriesFavoriteAndRatingAtTheOriginalTimestamp()
    {
        var at = new DateTime(2019, 6, 1, 20, 0, 0, DateTimeKind.Utc);
        var events = new List<BehaviorEvent>();

        WatchHistoryImporter.AppendMovie(events, User, Item, new UserItemData
        {
            Key = "k",
            Played = true,
            PlayCount = 1,
            IsFavorite = true,
            Rating = 10,
            LastPlayedDate = at,
        });

        Assert.Contains(events, e => e.EventType == BehaviorEventType.MarkedFavorite);
        Assert.Contains(events, e => e.EventType == BehaviorEventType.Rated && e.Value == 10);

        // The decay is what makes old history mean less than new history. Stamping "now" on a 2019
        // watch would make every imported title look equally fresh and flatten the profile.
        Assert.All(events, e => Assert.Equal(at, e.Timestamp));
        Assert.All(events, e => Assert.Equal(WatchHistoryImporter.ImportContext, e.ContextJson));
    }

    [Fact]
    public void ALowRatingIsANegativeSignal()
    {
        var events = new List<BehaviorEvent>();
        WatchHistoryImporter.AppendMovie(events, User, Item, new UserItemData { Key = "k", Rating = 1 });

        Assert.True(Weight(events) < 0);
    }

    [Fact]
    public void SeriesRollUpToOneItemAndCannotOutshoutAFilmByLength()
    {
        var at = DateTime.UtcNow.AddDays(-5);
        var longRun = new WatchHistoryImporter.SeriesTally();
        for (var i = 0; i < 60; i++)
        {
            longRun.Absorb(new UserItemData { Key = "k", Played = true, PlayCount = 1, LastPlayedDate = at });
        }

        var series = new List<BehaviorEvent>();
        longRun.Append(series, User, Item);

        var movie = new List<BehaviorEvent>();
        WatchHistoryImporter.AppendMovie(movie, User, Item, new UserItemData { Key = "k", Played = true, PlayCount = 1, LastPlayedDate = at });

        Assert.All(series, e => Assert.Equal(Item, e.JellyfinItemId));
        Assert.True(Weight(series) > Weight(movie));
        Assert.True(Weight(series) < Weight(movie) * 10);
    }

    [Fact]
    public void SeriesTakesItsNewestEpisodeAsTheTimestamp()
    {
        var old = new DateTime(2015, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var recent = new DateTime(2024, 9, 9, 0, 0, 0, DateTimeKind.Utc);

        var tally = new WatchHistoryImporter.SeriesTally();
        tally.Absorb(new UserItemData { Key = "k", Played = true, LastPlayedDate = old });
        tally.Absorb(new UserItemData { Key = "k", Played = true, LastPlayedDate = recent });

        var events = new List<BehaviorEvent>();
        tally.Append(events, User, Item);

        Assert.All(events, e => Assert.Equal(recent, e.Timestamp));
    }

    [Fact]
    public void SeriesCollapsesManyEpisodeRatingsIntoOne()
    {
        var tally = new WatchHistoryImporter.SeriesTally();
        tally.Absorb(new UserItemData { Key = "k", Rating = 8, IsFavorite = true });
        tally.Absorb(new UserItemData { Key = "k", Rating = 10, IsFavorite = true });

        var events = new List<BehaviorEvent>();
        tally.Append(events, User, Item);

        Assert.Single(events, e => e.EventType == BehaviorEventType.Rated);
        Assert.Single(events, e => e.EventType == BehaviorEventType.MarkedFavorite);
        Assert.Equal(9, events.Single(e => e.EventType == BehaviorEventType.Rated).Value);
    }

    [Fact]
    public void ASeriesFavoritedButNeverPlayedStillCounts()
    {
        var tally = new WatchHistoryImporter.SeriesTally();
        tally.Absorb(new UserItemData { Key = "k", IsFavorite = true });

        var events = new List<BehaviorEvent>();
        tally.Append(events, User, Item);

        Assert.Single(events);
        Assert.Equal(BehaviorEventType.MarkedFavorite, events[0].EventType);
    }
}
