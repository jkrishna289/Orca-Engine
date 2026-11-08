using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using Wholphin.Engine.Behavior;
using Wholphin.Engine.Data.Entities;
using Wholphin.Engine.Data.Enums;
using Wholphin.Engine.Personalization;
using Wholphin.Engine.Recommendation;
using Xunit;

namespace Wholphin.Engine.Tests;

/// <summary>
/// What reaches the taste vector, and how For You turns a ranking into the day's row.
/// </summary>
public class RecommendationSignalTests
{
    private static readonly CatalogItem Film = new() { Id = 1, MediaType = MediaType.Movie };
    private static readonly CatalogItem Show = new() { Id = 2, MediaType = MediaType.Series };

    private static BehaviorEvent Ev(BehaviorEventType type, double value, int daysAgo, CatalogItem item) =>
        new() { EventType = type, Value = value, Timestamp = DateTime.UtcNow.AddDays(-daysAgo), CatalogItemId = item.Id };

    private static List<(BehaviorEvent Event, CatalogItem Item)> Countable(params BehaviorEvent[] events) =>
        PersonalizationService.Countable(events, e => e.CatalogItemId == Film.Id ? Film : Show);

    [Fact]
    public void FinishesPerTitleAreCappedKeepingTheNewest()
    {
        // Live capture and the daily import both record the same watches; a binge must not count 20x.
        var binge = Enumerable.Range(0, 20).Select(d => Ev(BehaviorEventType.PlaybackCompleted, 1, d, Show)).ToArray();
        var rewatches = Enumerable.Range(0, 6).Select(d => Ev(BehaviorEventType.PlaybackCompleted, 1, d, Film)).ToArray();

        var shows = Countable(binge);
        Assert.Equal(SignalWeights.MaxFinishesPerSeries, shows.Count);
        Assert.All(shows, x => Assert.True(x.Event.Timestamp > DateTime.UtcNow.AddDays(-SignalWeights.MaxFinishesPerSeries)));
        Assert.Equal(SignalWeights.MaxFinishesPerFilm, Countable(rewatches).Count);
    }

    [Fact]
    public void AnEarlyStopOnlyCountsAgainstTitlesNeverFinished()
    {
        var interrupted = Countable(
            Ev(BehaviorEventType.PlaybackCompleted, 1, 3, Show),
            Ev(BehaviorEventType.PlaybackStopped, 0.05, 1, Show));
        Assert.DoesNotContain(interrupted, x => x.Event.EventType == BehaviorEventType.PlaybackStopped);

        var sampled = Countable(Ev(BehaviorEventType.PlaybackStopped, 0.05, 1, Film));
        Assert.Single(sampled);
    }

    [Fact]
    public void TheDailyRowIsStableWithinADayAndChangesAcrossDays()
    {
        var pool = Enumerable.Range(0, 40).ToList();
        var user = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var day = new DateOnly(2026, 11, 8);

        var first = ContentRecommender.RotateDaily(pool, 20, user, day);
        Assert.Equal(first, ContentRecommender.RotateDaily(pool, 20, user, day));
        Assert.NotEqual(first, ContentRecommender.RotateDaily(pool, 20, user, day.AddDays(1)));
        Assert.Equal(20, first.Count);
        Assert.Equal(first.OrderBy(x => x), first);
    }

    [Fact]
    public void TheBestMatchStillShowsOnMostDays()
    {
        var pool = Enumerable.Range(0, 40).ToList();
        var user = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var start = new DateOnly(2026, 1, 1);

        var days = Enumerable.Range(0, 200).Select(d => ContentRecommender.RotateDaily(pool, 20, user, start.AddDays(d))).ToList();

        Assert.True(days.Count(d => d.Contains(0)) >= 160);
        Assert.True(days.Count(d => d.Contains(39)) < days.Count(d => d.Contains(0)));
    }

    [Fact]
    public void EpisodeEventsBelongToTheirSeries()
    {
        var series = Guid.NewGuid();
        var episode = new Episode { Id = Guid.NewGuid(), SeriesId = series };
        var movie = new Movie { Id = Guid.NewGuid() };

        Assert.Equal(series, BehaviorEntryPoint.TasteItemId(episode));
        Assert.Equal(movie.Id, BehaviorEntryPoint.TasteItemId(movie));
    }
}
