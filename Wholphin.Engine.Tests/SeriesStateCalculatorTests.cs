using System;
using System.Collections.Generic;
using Wholphin.Engine.Intelligence;
using Xunit;

namespace Wholphin.Engine.Tests;

/// <summary>Unit tests for the pure per-series user-state classification rules.</summary>
public class SeriesStateCalculatorTests
{
    private static readonly DateTime Now = new(2026, 7, 5, 0, 0, 0, DateTimeKind.Utc);

    private static SeasonProgress S(int season, int episodes, int watched) => new(season, episodes, watched);

    [Fact]
    public void NoEpisodesWatched_IsNotTracked()
    {
        var result = SeriesStateCalculator.Derive(new[] { S(1, 10, 0), S(2, 10, 0) }, null, Now);

        Assert.Equal(SeriesUserState.NotTracked, result.State);
        Assert.Equal(0, result.WatchedEpisodes);
        Assert.Equal(20, result.TotalEpisodes);
        Assert.Null(result.GapSeasonNumber);
    }

    [Fact]
    public void EveryAvailableEpisodeWatched_IsCompleted()
    {
        var result = SeriesStateCalculator.Derive(new[] { S(1, 10, 10), S(2, 8, 8) }, Now.AddDays(-200), Now);

        Assert.Equal(SeriesUserState.Completed, result.State);
        Assert.Equal(18, result.WatchedEpisodes);
        Assert.Equal(18, result.TotalEpisodes);
    }

    [Fact]
    public void SkippedEarlierSeasonWhileLaterWatched_IsSkippedBacklogAtEarliestGap()
    {
        // The spec's canonical case: S1 unwatched, S2+S3 watched, S4 not yet reached.
        var seasons = new[] { S(1, 10, 0), S(2, 10, 10), S(3, 10, 10), S(4, 10, 0) };

        var result = SeriesStateCalculator.Derive(seasons, Now.AddDays(-1), Now);

        Assert.Equal(SeriesUserState.SkippedBacklog, result.State);
        Assert.Equal(1, result.GapSeasonNumber);
    }

    [Fact]
    public void GapBetweenWatchedSeasons_ReportsTheGapSeason()
    {
        var result = SeriesStateCalculator.Derive(new[] { S(1, 10, 10), S(2, 10, 0), S(3, 10, 5) }, Now.AddDays(-1), Now);

        Assert.Equal(SeriesUserState.SkippedBacklog, result.State);
        Assert.Equal(2, result.GapSeasonNumber);
    }

    [Fact]
    public void UnwatchedSeasonAboveHighestTouched_IsNotAGap()
    {
        // S1 fully watched, S2 not started — that's just "not yet reached", so recent → Watching.
        var result = SeriesStateCalculator.Derive(new[] { S(1, 10, 10), S(2, 10, 0) }, Now.AddDays(-2), Now);

        Assert.Equal(SeriesUserState.Watching, result.State);
        Assert.Null(result.GapSeasonNumber);
    }

    [Fact]
    public void ContiguousProgressWithRecentActivity_IsWatching()
    {
        var result = SeriesStateCalculator.Derive(new[] { S(1, 10, 4) }, Now.AddDays(-3), Now);

        Assert.Equal(SeriesUserState.Watching, result.State);
    }

    [Fact]
    public void ContiguousProgressGoneStale_IsAbandoned()
    {
        var result = SeriesStateCalculator.Derive(new[] { S(1, 10, 4) }, Now.AddDays(-60), Now);

        Assert.Equal(SeriesUserState.Abandoned, result.State);
    }

    [Fact]
    public void Specials_AreIgnored()
    {
        // Season 0 (specials) must not count toward completion or trigger a false gap.
        var seasons = new[] { S(0, 5, 5), S(1, 10, 4) };

        var result = SeriesStateCalculator.Derive(seasons, Now.AddDays(-2), Now);

        Assert.Equal(SeriesUserState.Watching, result.State);
        Assert.Equal(4, result.WatchedEpisodes);
        Assert.Equal(10, result.TotalEpisodes);
    }

    [Fact]
    public void OnlySpecialsWatched_IsNotTracked()
    {
        var result = SeriesStateCalculator.Derive(new List<SeasonProgress> { S(0, 5, 5), S(1, 10, 0) }, Now.AddDays(-2), Now);

        Assert.Equal(SeriesUserState.NotTracked, result.State);
    }
}
