using System;
using System.Collections.Generic;
using System.Linq;
using Wholphin.Engine.Streaming;
using Xunit;

namespace Wholphin.Engine.Tests;

/// <summary>
/// Unit tests for the piece-cache eviction decision.
///
/// This logic exists because stream teardown stopped deleting downloaded data — keeping it is what
/// lets a paused film resume without re-downloading — so something has to bound the folder instead.
/// It picks files to destroy, which is reason enough not to trust it by inspection.
/// </summary>
public class StreamCacheTrimTests
{
    private const long Gb = 1024L * 1024 * 1024;

    private static TorrentStreamService.CachedFile File(string name, long length, int ageDays) =>
        new(name, length, DateTime.UtcNow.AddDays(-ageDays));

    [Fact]
    public void UnderBudget_EvictsNothing()
    {
        var files = new[] { File("a", 3 * Gb, 10), File("b", 4 * Gb, 1) };

        var victims = TorrentStreamService.SelectForEviction(files, 20 * Gb);

        Assert.Empty(victims);
    }

    [Fact]
    public void OverBudget_DropsLeastRecentlyAccessedFirst_AndStopsOnceUnder()
    {
        // 24GB against a 20GB budget: dropping the single oldest 5GB file is enough, so the second
        // oldest must survive. Evicting more than necessary would throw away a swarm for nothing.
        var files = new[]
        {
            File("oldest", 5 * Gb, 30),
            File("middle", 9 * Gb, 10),
            File("newest", 10 * Gb, 1),
        };

        var victims = TorrentStreamService.SelectForEviction(files, 20 * Gb);

        Assert.Equal(new[] { "oldest" }, victims.Select(v => v.Path));
    }

    [Fact]
    public void FarOverBudget_KeepsDroppingUntilItFits()
    {
        var files = new[]
        {
            File("oldest", 8 * Gb, 30),
            File("middle", 8 * Gb, 20),
            File("newest", 8 * Gb, 1),
        };

        var victims = TorrentStreamService.SelectForEviction(files, 10 * Gb);

        // 24GB down to 8GB takes both older files; the newest is what a viewer is most likely to
        // come back to, so it is the last thing standing.
        Assert.Equal(new[] { "oldest", "middle" }, victims.Select(v => v.Path));
    }

    [Fact]
    public void EmptyCache_EvictsNothing()
    {
        var victims = TorrentStreamService.SelectForEviction(new List<TorrentStreamService.CachedFile>(), Gb);

        Assert.Empty(victims);
    }
}
