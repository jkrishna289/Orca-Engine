using System;
using System.Linq;
using Wholphin.Engine.Streaming;
using Xunit;

namespace Wholphin.Engine.Tests;

/// <summary>
/// Unit tests for parsing ngosang/trackerslist.
///
/// This list is the whole of peer discovery — MonoTorrent's DHT never bootstraps — so a parsing slip
/// here reads downstream as "this torrent has no seeders", which is the single most misleading way
/// this feature fails.
/// </summary>
public class TrackerListTests
{
    // The real file's shape: one URL per line, separated by blank lines, trailing newline.
    private const string RealShape = """
    udp://open.demonii.com:1337/announce

    udp://tracker.opentrackr.org:1337/announce

    udp://open.stealth.si:80/announce

    """;

    [Fact]
    public void ParsesTheUpstreamFormat_IgnoringBlankSeparators()
    {
        var parsed = TrackerList.Parse(RealShape);

        Assert.Equal(
            new[]
            {
                "udp://open.demonii.com:1337/announce",
                "udp://tracker.opentrackr.org:1337/announce",
                "udp://open.stealth.si:80/announce",
            },
            parsed);
    }

    [Fact]
    public void DropsWebTorrentEntries()
    {
        // Sibling lists (trackers_all) carry ws:// entries. MonoTorrent cannot use them; announcing
        // to one only produces errors, so they must never reach a torrent.
        var parsed = TrackerList.Parse(
            "udp://tracker.opentrackr.org:1337/announce\n"
            + "ws://tracker.openwebtorrent.com:443/announce\n"
            + "wss://tracker.btorrent.xyz\n");

        Assert.Equal(["udp://tracker.opentrackr.org:1337/announce"], parsed);
    }

    [Fact]
    public void IgnoresCommentsAndWhitespace()
    {
        var parsed = TrackerList.Parse(
            "# generated daily\n   \nudp://open.stealth.si:80/announce   \n\t\n");

        Assert.Equal(["udp://open.stealth.si:80/announce"], parsed);
    }

    [Fact]
    public void DeduplicatesCaseInsensitively()
    {
        var parsed = TrackerList.Parse(
            "udp://tracker.opentrackr.org:1337/announce\n\nUDP://Tracker.OpenTrackr.org:1337/announce\n");

        Assert.Single(parsed);
    }

    [Fact]
    public void CapsTheListSoAnnounceChurnStaysBounded()
    {
        // A bad upstream edit must not turn into hundreds of announces per stream — that churn is
        // what previously exhausted the household router's NAT table.
        var many = string.Join("\n\n", Enumerable.Range(1, 200).Select(i => $"udp://t{i}.example.org:6969/announce"));

        Assert.Equal(TrackerList.MaxTrackers, TrackerList.Parse(many).Length);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n\n  \n")]
    [InlineData("not a url at all\nalso: nonsense\n")]
    public void GarbageYieldsNothing_SoTheCallerFallsBack(string body)
    {
        Assert.Empty(TrackerList.Parse(body));
    }

    [Fact]
    public void FallbackIsUsableOnItsOwn()
    {
        // The fallback ships as the answer whenever the fetch fails, so it must satisfy every rule
        // the parser enforces on upstream content.
        Assert.NotEmpty(TrackerList.Fallback);
        Assert.All(TrackerList.Fallback, t => Assert.StartsWith("udp://", t, StringComparison.Ordinal));
        Assert.All(TrackerList.Fallback, t => Assert.True(Uri.TryCreate(t, UriKind.Absolute, out _)));
        Assert.Equal(TrackerList.Fallback.Length, TrackerList.Fallback.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
