using Wholphin.Engine.Streaming;
using Xunit;

namespace Wholphin.Engine.Tests;

/// <summary>
/// Unit tests for the reachability verdict and the session diagnosis.
///
/// Both exist to stop an operator being told something the evidence does not support. The verdict
/// must never call a server reachable on the strength of a router agreeing to a mapping, and the
/// diagnosis must never recommend raising the connection limit — the change that twice took the
/// household network offline — on evidence that cannot distinguish a limit from unreachability.
/// </summary>
public class StreamDiagnosticsTests
{
    [Fact]
    public void NoEngine_IsUnknownNotUnreachable()
    {
        // Before the first stream nothing is listening, which says nothing about the network.
        Assert.Equal(
            InboundReachability.Unknown,
            TorrentStreamService.Verdict(listenerBound: false, inboundPeers: 0, engineExists: false));
    }

    [Fact]
    public void ListenerNotBound_IsNotReachable()
    {
        Assert.Equal(
            InboundReachability.NotReachable,
            TorrentStreamService.Verdict(listenerBound: false, inboundPeers: 0, engineExists: true));
    }

    [Fact]
    public void BoundButNobodyArrived_IsPending()
    {
        // Absence of proof, not proof of absence: an idle swarm simply may not have dialled us yet.
        Assert.Equal(
            InboundReachability.Pending,
            TorrentStreamService.Verdict(listenerBound: true, inboundPeers: 0, engineExists: true));
    }

    [Fact]
    public void AnInboundPeer_IsTheOnlyThingThatProvesReachability()
    {
        Assert.Equal(
            InboundReachability.Reachable,
            TorrentStreamService.Verdict(listenerBound: true, inboundPeers: 1, engineExists: true));
    }

    [Fact]
    public void MissingMetadata_IsReportedAsTheWait()
    {
        // The 2026-08-16 failure: a source sat in this state for two minutes with no log line, no
        // timeout, and nothing on screen to distinguish it from one about to succeed.
        var d = StreamDiagnostics.Diagnose(
            "Preparing", hasMetadata: false, openConnections: 0, maxConnections: 80,
            seeds: 0, swarmRateBytes: 0, requiredBytes: 0, stalls: 0, InboundReachability.Pending);

        Assert.Contains("file list", d.Headline);
        Assert.Contains("another source", d.Recommendation);
    }

    [Fact]
    public void PinnedRampAndStarved_RecommendsProvingReachabilityNotRaisingLimits()
    {
        // The measured case: 8/8 connections, 56 seeds, 1.18 MB/s swarm against 908 KB/s required.
        var d = StreamDiagnostics.Diagnose(
            "Ready", hasMetadata: true, openConnections: 8, maxConnections: 8,
            seeds: 56, swarmRateBytes: 700_000, requiredBytes: 908_487, stalls: 4,
            InboundReachability.Pending);

        Assert.Equal("bad", d.Severity);
        Assert.Contains("inbound reachability", d.Recommendation, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("raise", d.Recommendation, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PinnedRampWhenAlreadyReachable_MayMentionTheCeiling()
    {
        // Once inbound peers are proven, the ceiling really is the remaining constraint — and only
        // then is it honest to say so.
        var d = StreamDiagnostics.Diagnose(
            "Ready", hasMetadata: true, openConnections: 80, maxConnections: 80,
            seeds: 56, swarmRateBytes: 700_000, requiredBytes: 908_487, stalls: 4,
            InboundReachability.Reachable);

        Assert.Contains("ceiling", d.Recommendation, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StarvedWithHeadroom_BlamesTheSwarmNotTheLimits()
    {
        var d = StreamDiagnostics.Diagnose(
            "Ready", hasMetadata: true, openConnections: 3, maxConnections: 80,
            seeds: 2, swarmRateBytes: 100_000, requiredBytes: 908_487, stalls: 6,
            InboundReachability.Reachable);

        Assert.Equal("bad", d.Severity);
        Assert.Contains("another source", d.Recommendation);
    }

    [Fact]
    public void HealthyStream_SaysSoWithoutARecommendation()
    {
        var d = StreamDiagnostics.Diagnose(
            "Ready", hasMetadata: true, openConnections: 20, maxConnections: 80,
            seeds: 40, swarmRateBytes: 2_000_000, requiredBytes: 908_487, stalls: 0,
            InboundReachability.Reachable);

        Assert.Equal("ok", d.Severity);
        Assert.Equal(string.Empty, d.Recommendation);
    }

    [Fact]
    public void UnknownBitrate_DoesNotProduceAStarvedVerdict()
    {
        // ffprobe returning nothing is a supported outcome. Treating a missing bitrate as 0 required
        // would otherwise report every such stream as healthy or starved on no evidence at all.
        var d = StreamDiagnostics.Diagnose(
            "Ready", hasMetadata: true, openConnections: 5, maxConnections: 80,
            seeds: 10, swarmRateBytes: 50_000, requiredBytes: 0, stalls: 0,
            InboundReachability.Pending);

        Assert.Equal("ok", d.Severity);
    }

    [Fact]
    public void ForwardingOff_IsNotAFailure()
    {
        Assert.Equal(
            PortForwardState.Off,
            TorrentStreamService.ForwardState(requested: false, created: false, pending: false, inboundPeers: 0));
    }

    [Fact]
    public void RouterRefused_IsNotWorking()
    {
        Assert.Equal(
            PortForwardState.NotWorking,
            TorrentStreamService.ForwardState(requested: true, created: false, pending: false, inboundPeers: 0));
    }

    [Fact]
    public void StillNegotiating_IsNotYetAFailure()
    {
        Assert.Equal(
            PortForwardState.Mapped,
            TorrentStreamService.ForwardState(requested: true, created: false, pending: true, inboundPeers: 0));
    }

    [Fact]
    public void CreatedMapping_IsMappedNotWorking()
    {
        // The distinction this whole state machine exists for: the router agreeing is not a packet
        // arriving. CGNAT and an upstream router both leave a created mapping carrying nothing.
        Assert.Equal(
            PortForwardState.Mapped,
            TorrentStreamService.ForwardState(requested: true, created: true, pending: false, inboundPeers: 0));
    }

    [Fact]
    public void AnInboundPeer_IsWhatProvesForwardingWorks()
    {
        Assert.Equal(
            PortForwardState.Working,
            TorrentStreamService.ForwardState(requested: true, created: true, pending: false, inboundPeers: 1));
    }

    [Fact]
    public void InboundPeerWithoutAMapping_StillCountsAsWorking()
    {
        // A manual router forward produces no UPnP mapping at all, and it is the configuration most
        // likely to be in place after someone follows this dashboard's own advice.
        Assert.Equal(
            PortForwardState.Working,
            TorrentStreamService.ForwardState(requested: true, created: false, pending: false, inboundPeers: 3));
    }

    [Fact]
    public void UnreadSession_IsPausedSoItStopsUsingBandwidth()
    {
        // Measured 2026-08-16: after playback ended the torrent kept pulling ~1 MB/s across 40 peers
        // for the full 20-minute idle window, for a film nobody was watching.
        var now = System.DateTimeOffset.UtcNow;

        Assert.True(TorrentStreamService.ShouldPause(kept: false, now.AddMinutes(-5), now));
    }

    [Fact]
    public void RecentlyReadSession_IsLeftAlone()
    {
        // A player with a deep buffer legitimately stops reading for a while; pausing mid-film would
        // turn a quiet minute into a stall.
        var now = System.DateTimeOffset.UtcNow;

        Assert.False(TorrentStreamService.ShouldPause(kept: false, now.AddSeconds(-30), now));
    }

    [Fact]
    public void KeptSession_IsNeverPaused()
    {
        // Keeping exists precisely to finish a download with nobody reading it, which looks identical
        // to abandonment by every other measure here.
        var now = System.DateTimeOffset.UtcNow;

        Assert.False(TorrentStreamService.ShouldPause(kept: true, now.AddHours(-1), now));
    }

    [Fact]
    public void FailedSession_SaysSoPlainly()
    {
        var d = StreamDiagnostics.Diagnose(
            "Failed", hasMetadata: true, openConnections: 0, maxConnections: 80,
            seeds: 0, swarmRateBytes: 0, requiredBytes: 0, stalls: 0, InboundReachability.Pending);

        Assert.Equal("bad", d.Severity);
        Assert.Contains("another source", d.Recommendation);
    }
}
