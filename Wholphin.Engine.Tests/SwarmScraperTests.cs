using System;
using Wholphin.Engine.Streaming;
using Xunit;

namespace Wholphin.Engine.Tests;

/// <summary>
/// Unit tests for infohash extraction, the step that decides whether a source can be verified at all.
///
/// A silent failure here is invisible and expensive: the hash simply never gets scraped, the source
/// keeps the indexer's invented seeder count, and a dead torrent is offered to the viewer as though
/// it were healthy. Both magnet encodings occur in real indexer output.
/// </summary>
public class SwarmScraperTests
{
    // Big Buck Bunny, the torrent used throughout testing. Hex and Base32 of the same infohash.
    private const string Hex = "dd8255ecdc7ca55fb0bbf81323d87062db1f6d1c";
    private const string Base32 = "3WBFL3G4PSSV7MF37AJSHWDQMLNR63I4";

    [Fact]
    public void ExtractsHexInfoHash()
    {
        var magnet = $"magnet:?xt=urn:btih:{Hex}&dn=Big+Buck+Bunny&tr=udp%3A%2F%2Ftracker.example%3A1337";

        Assert.Equal(Hex, SwarmScraper.InfoHashOf(magnet));
    }

    [Fact]
    public void ExtractsBase32InfoHash_NormalisedToHex()
    {
        // Real indexers emit Base32 magnets; scraping needs the raw 20 bytes either way.
        var magnet = $"magnet:?xt=urn:btih:{Base32}&dn=Big+Buck+Bunny";

        Assert.Equal(Hex, SwarmScraper.InfoHashOf(magnet));
    }

    [Fact]
    public void IsCaseInsensitiveAndNormalisesToLowercase()
    {
        var magnet = $"magnet:?XT=URN:BTIH:{Hex.ToUpperInvariant()}";

        Assert.Equal(Hex, SwarmScraper.InfoHashOf(magnet));
    }

    [Theory]
    // An HTTP .torrent proxy link — common from Prowlarr, and deliberately unverifiable here because
    // learning its infohash would cost a fetch per search result.
    [InlineData("http://prowlarr.local/1/download?apikey=secret&link=abc")]
    [InlineData("")]
    [InlineData(null)]
    // Well-formed magnet, but a v2-only (SHA-256) hash: not a 20-byte v1 hash, so not scrapeable.
    [InlineData("magnet:?xt=urn:btih:" + "a" + Hex + "abcdefghijklmnopqrstuvwxyz")]
    // btmh is BitTorrent v2's multihash form, not btih.
    [InlineData("magnet:?xt=urn:btmh:1220caf1e1c30e81cb361b9ee167c4a3d1e5f1e1c30e81cb361b9ee167c4a3d1e5f1")]
    public void RejectsWhatCannotBeScraped(string? url)
    {
        Assert.Null(SwarmScraper.InfoHashOf(url));
    }

    [Fact]
    public void RejectsMalformedBase32RatherThanGuessing()
    {
        // '1' and '8' are not in the RFC 4648 Base32 alphabet.
        Assert.Null(SwarmScraper.FromBase32("11111111111111111111111111111111"));
        Assert.Null(SwarmScraper.FromBase32("SHORT"));
    }

    [Fact]
    public void Base32DecodesToExactlyTwentyBytes()
    {
        var decoded = SwarmScraper.FromBase32(Base32);

        Assert.NotNull(decoded);
        Assert.Equal(20, decoded!.Length);
        Assert.Equal(Hex, Convert.ToHexString(decoded).ToLowerInvariant());
    }

    [Theory]
    [InlineData(Hex, Hex)]                              // already canonical
    [InlineData("DD8255ECDC7CA55FB0BBF81323D87062DB1F6D1C", Hex)]  // uppercase hex
    [InlineData("  " + Hex + "  ", Hex)]                // indexers pad it
    [InlineData(Base32, Hex)]                           // Base32 form
    public void NormalisesIndexerReportedInfoHashes(string reported, string expected)
    {
        Assert.Equal(expected, SwarmScraper.Normalise(reported));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-hash")]
    [InlineData("dd8255ecdc7ca55fb0bbf81323d87062db1f6d")]   // 38 chars, truncated
    [InlineData("caf1e1c30e81cb361b9ee167c4a3d1e5f1e1c30e81cb361b9ee167c4a3d1e5f1")] // v2 SHA-256
    public void RejectsAnythingThatIsNotAV1InfoHash(string? reported)
    {
        // Scraping a wrong hash silently reports a different torrent's swarm as this one's, which is
        // worse than reporting nothing.
        Assert.Null(SwarmScraper.Normalise(reported));
    }

    [Fact]
    public void BatchSizeRespectsTheProtocolLimit()
    {
        // BEP-15 caps a scrape at 74 infohashes; exceeding it gets the request dropped silently,
        // which would look exactly like "every source is dead".
        Assert.True(SwarmScraper.HashesPerScrape <= 74);
    }
}
