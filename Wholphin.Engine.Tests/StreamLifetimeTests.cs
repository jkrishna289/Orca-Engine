using Wholphin.Engine.Streaming;
using Xunit;

namespace Wholphin.Engine.Tests;

/// <summary>
/// Stremio-style session lifetime: anything holding the session (an open file request, a lease, a
/// lease-less opener) keeps it alive; it is retired a grace period after the last hold goes.
/// </summary>
public class StreamLifetimeTests
{
    private static readonly TimeSpan Grace = TorrentStreamService.ReleaseGrace;
    private static readonly TimeSpan Idle = TimeSpan.FromMinutes(20);

    private static StreamSession NewSession() => new() { Id = "abc", Token = "t" };

    private static DateTimeOffset In(TimeSpan offset) => DateTimeOffset.UtcNow + offset;

    [Fact]
    public void OneViewerLeaving_DoesNotRetireASharedTorrent()
    {
        var session = NewSession();
        var a = Guid.NewGuid();
        session.AcquireViewer(a);
        session.AcquireViewer(Guid.NewGuid());

        Assert.True(session.ReleaseViewer(a));
        Assert.False(session.ReleaseViewer(a));
        Assert.Null(session.RestartGrace());
        Assert.False(session.IsDue(In(Grace * 2), Grace, Idle));
    }

    [Fact]
    public void LastHoldGoing_StartsACancelableGrace()
    {
        var session = NewSession();
        var lease = Guid.NewGuid();
        session.AcquireViewer(lease);
        session.ReleaseViewer(lease);

        var grace = session.RestartGrace();
        Assert.NotNull(grace);
        Assert.False(session.IsDue(In(Grace / 2), Grace, Idle));
        Assert.True(session.IsDue(In(Grace + TimeSpan.FromSeconds(1)), Grace, Idle));

        // Coming back inside the grace cancels the countdown.
        Assert.True(session.TryBeginRead());
        Assert.Null(session.RestartGrace());
        Assert.True(grace!.Value.IsCancellationRequested);
        Assert.False(session.IsDue(In(Grace * 2), Grace, Idle));
    }

    [Fact]
    public void AnOpenRequest_HoldsTheSession_AndARetiredSessionRefusesNewReads()
    {
        var session = NewSession();
        Assert.True(session.TryBeginRead());
        Assert.False(session.TryRetire(In(Grace * 2), Grace, Idle));

        session.EndRead();
        Assert.True(session.TryRetire(In(Grace * 2), Grace, Idle));
        Assert.False(session.TryRetire(In(Grace * 2), Grace, Idle));
        Assert.False(session.TryBeginRead());
    }

    [Fact]
    public void LegacyStop_ReleasesOneHoldAtATime()
    {
        var session = NewSession();
        session.AcquireViewer(null);
        session.AcquireViewer(null);

        Assert.True(session.ReleaseLegacyViewer());
        Assert.False(session.IsDue(In(Grace * 2), Grace, Idle));
        Assert.True(session.ReleaseLegacyViewer());
        Assert.False(session.ReleaseLegacyViewer());
        Assert.True(session.IsDue(In(Grace * 2), Grace, Idle));
    }

    [Fact]
    public void ALeakedHold_IsStillRetiredAfterTheIdleWindow_ButAKeptSessionNeverIs()
    {
        var session = NewSession();
        session.AcquireViewer(null); // opened, never stopped
        Assert.False(session.IsDue(In(Idle / 2), Grace, Idle));
        Assert.True(session.IsDue(In(Idle + TimeSpan.FromSeconds(1)), Grace, Idle));

        session.Kept = true;
        Assert.False(session.IsDue(In(Idle * 2), Grace, Idle));
        Assert.Null(session.RestartGrace());
    }
}
