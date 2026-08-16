using System.IO;
using System.Linq;
using MonoTorrent.Connections;
using Wholphin.Engine.Configuration;
using Wholphin.Engine.Streaming;
using Xunit;

namespace Wholphin.Engine.Tests;

/// <summary>
/// Unit tests for turning stored configuration into MonoTorrent engine settings.
///
/// These are the pure edges of settings that are otherwise only observable by watching a swarm. A
/// slip here is silent in the worst way: the wrong encryption list quietly shrinks the usable swarm,
/// and a port that falls back to random defeats port forwarding entirely while every screen still
/// reports the feature as on.
/// </summary>
public class StreamSettingsTests
{
    // Any path will do — ToSettings does not touch the filesystem; only ClientEngine's own validation
    // creates directories, and no engine is constructed here.
    private static readonly string CacheDir = Path.Combine(Path.GetTempPath(), "orca-tests");

    private static PluginConfiguration Config() => new();

    [Fact]
    public void AllowMode_OffersEncryptionFirstButAcceptsPlaintext()
    {
        var types = TorrentStreamService.EncryptionFor("allow");

        // Order is preference order — MonoTorrent tries them in sequence, so plaintext must come last
        // or an encrypted-capable peer would be connected to in the clear.
        Assert.Equal(
            new[] { EncryptionType.RC4Header, EncryptionType.RC4Full, EncryptionType.PlainText },
            types);
    }

    [Fact]
    public void RequireMode_RefusesPlaintext()
    {
        Assert.DoesNotContain(EncryptionType.PlainText, TorrentStreamService.EncryptionFor("require"));
    }

    [Fact]
    public void DisableMode_OffersOnlyPlaintext()
    {
        Assert.Equal(new[] { EncryptionType.PlainText }, TorrentStreamService.EncryptionFor("disable"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("Allow")]
    [InlineData("nonsense")]
    public void UnknownMode_FallsBackToAllow(string? mode)
    {
        // A typo in a settings field must never be able to stop every stream on the server, and casing
        // must not matter — the value round-trips through an HTML select and an XML file.
        Assert.Equal(TorrentStreamService.EncryptionFor("allow"), TorrentStreamService.EncryptionFor(mode));
    }

    [Theory]
    [InlineData(51413, 51413)]
    [InlineData(1, 1)]
    [InlineData(65535, 65535)]
    public void ValidPorts_AreKept(int configured, int expected)
    {
        Assert.Equal(expected, TorrentStreamService.ClampPort(configured));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    [InlineData(int.MaxValue)]
    public void InvalidPorts_BecomeAny(int configured)
    {
        // 0 is MonoTorrent's "pick one for me". Anything out of range lands there too rather than
        // throwing at engine construction, which would take streaming down over a mistyped digit.
        Assert.Equal(0, TorrentStreamService.ClampPort(configured));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-5, 0)]
    [InlineData(1, 1024)]
    [InlineData(1500, 1536000)]
    public void RateCaps_ConvertKibibytesToBytes(int kibibytes, int expected)
    {
        // Zero has to survive as zero: MonoTorrent reads it as "unlimited", so rounding it up to a
        // 1-byte-per-second cap would stall every stream on the server.
        Assert.Equal(expected, TorrentStreamService.KibibytesToBytes(kibibytes));
    }

    [Fact]
    public void DhtSharesThePeerPort_SoOneFirewallRuleCoversBoth()
    {
        // TCP peer listener and UDP DHT on the same number is what qBittorrent, Transmission and
        // Deluge do, and it is why router guides say "open port N" rather than naming two. On a random
        // DHT port — MonoTorrent's default — DHT sits outside every rule an operator can write.
        var config = Config();
        config.StreamEnableDht = true;
        config.StreamListenPort = 51413;

        var settings = TorrentStreamService.BuildEngineSettings(config, CacheDir);

        Assert.NotNull(settings.DhtEndPoint);
        Assert.Equal(51413, settings.DhtEndPoint!.Port);
        Assert.Equal(settings.ListenEndPoints["ipv4"].Port, settings.DhtEndPoint.Port);
    }

    [Fact]
    public void DhtFallsBackToRandom_WhenThePeerPortIsRandomToo()
    {
        // Nothing to align with, so there is no point pinning it.
        var config = Config();
        config.StreamEnableDht = true;

        var settings = TorrentStreamService.BuildEngineSettings(config, CacheDir);

        Assert.NotNull(settings.DhtEndPoint);
        Assert.Equal(0, settings.DhtEndPoint!.Port);
    }

    [Fact]
    public void DhtDisabled_UsesNullEndpointRatherThanPortMinusOne()
    {
        // Regression guard. MonoTorrent's doc comment says "-1 disables", which describes an older
        // int-based API; IPEndPoint validates 0-65535, so constructing one with -1 throws before the
        // engine is ever reached. Disabling is expressed as a null endpoint, and MonoTorrent swaps in
        // a NullDhtListener. Getting this wrong makes the DHT switch a landmine rather than a setting.
        var config = Config();
        config.StreamEnableDht = false;

        var settings = TorrentStreamService.BuildEngineSettings(config, CacheDir);

        Assert.Null(settings.DhtEndPoint);
    }

    [Fact]
    public void ConfiguredListenPort_IsUsed()
    {
        var config = Config();
        config.StreamListenPort = 51413;

        var settings = TorrentStreamService.BuildEngineSettings(config, CacheDir);

        Assert.Equal(51413, settings.ListenEndPoints["ipv4"].Port);
    }

    [Fact]
    public void ZeroListenPort_MeansRandom()
    {
        var settings = TorrentStreamService.BuildEngineSettings(Config(), CacheDir);

        // The shipped default, and the reason inbound reachability was unprovable: a port that moves
        // every restart cannot be forwarded.
        Assert.Equal(0, settings.ListenEndPoints["ipv4"].Port);
    }

    [Fact]
    public void OutOfRangeListenPort_DoesNotThrow()
    {
        var config = Config();
        config.StreamListenPort = 70000;

        var settings = TorrentStreamService.BuildEngineSettings(config, CacheDir);

        Assert.Equal(0, settings.ListenEndPoints["ipv4"].Port);
    }

    [Fact]
    public void ReportedEndPoint_NeedsBothAddressAndPort()
    {
        var config = Config();
        config.StreamReportedAddress = "203.0.113.7";
        config.StreamReportedPort = 51413;

        var settings = TorrentStreamService.BuildEngineSettings(config, CacheDir);

        Assert.Equal("203.0.113.7:51413", settings.ReportedListenEndPoints["ipv4"].ToString());
    }

    [Theory]
    [InlineData("203.0.113.7", 0)]
    [InlineData("", 51413)]
    [InlineData("", 0)]
    [InlineData("not-an-address", 51413)]
    public void ReportedEndPoint_IsAbsentUnlessFullyAndValidlySpecified(string address, int port)
    {
        // Half a pair advertises an endpoint no peer can reach, which is worse than advertising
        // nothing: the engine would be telling trackers to send peers somewhere that cannot answer.
        var config = Config();
        config.StreamReportedAddress = address;
        config.StreamReportedPort = port;

        var settings = TorrentStreamService.BuildEngineSettings(config, CacheDir);

        Assert.Empty(settings.ReportedListenEndPoints);
    }

    [Fact]
    public void PeerBudgetDefaults_AreUnchanged()
    {
        // These are the values two LAN outages established as safe. A refactor that quietly moved
        // them would be the most expensive kind of regression here, so they are pinned.
        var settings = TorrentStreamService.BuildEngineSettings(Config(), CacheDir);

        Assert.Equal(80, settings.MaximumConnections);
        Assert.Equal(8, settings.MaximumHalfOpenConnections);
        Assert.Equal(10, (int)settings.ConnectionTimeout.TotalSeconds);
        Assert.Equal(0, settings.MaximumDownloadRate);
        Assert.Equal(0, settings.MaximumUploadRate);
    }

    [Fact]
    public void PrivacyDefaults_MatchThePreviousHardcodedBehaviour()
    {
        var settings = TorrentStreamService.BuildEngineSettings(Config(), CacheDir);

        Assert.True(settings.AllowPortForwarding);
        Assert.True(settings.AllowLocalPeerDiscovery);
        Assert.Equal(TorrentStreamService.EncryptionFor("allow"), settings.AllowedEncryption.ToList());
    }
}
