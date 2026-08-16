using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Wholphin.Engine.Streaming;

/// <summary>
/// Replaces indexer-claimed seeder counts with counts measured from the actual trackers.
/// </summary>
/// <remarks>
/// Indexers lie, and not by a little. Measured the same minute, 2026-08-12: LimeTorrents advertised
/// **186 seeders** for a swarm that really had **1** (it delivered 0.4 MB then stalled at 0 kB/s for
/// four minutes); The Pirate Bay advertised 5 for a torrent that yielded **zero peers in 180s**. The
/// engine ranked on those numbers and the picker printed them to the viewer as "N sharing", so people
/// were being invited to choose sources that could not possibly play.
///
/// This uses BEP-15 **scrape**, not announce, which matters: scrape asks a tracker what it knows
/// without joining the swarm, so searching does not advertise the server as a peer for every result
/// it merely looked at. It also batches — one connect plus one request covers up to
/// <see cref="HashesPerScrape"/> infohashes per tracker.
///
/// **Fails safe, deliberately.** <see cref="SourceRanker"/> discards anything below its minimum
/// seeder count, so zeroing a source out is destructive: a tracker outage that returned "0 peers" for
/// everything would empty the picker for content that is perfectly healthy. A claimed count is only
/// ever overwritten when a tracker actually answered for that infohash; otherwise it is left alone
/// and simply marked unverified.
/// </remarks>
public sealed class SwarmScraper
{
    /// <summary>BEP-15's cap on infohashes per scrape request.</summary>
    internal const int HashesPerScrape = 74;

    // Separate budgets, deliberately. These are sequential phases — infohashes must be known before
    // anything can be scraped — so sharing one budget meant slow link resolution starved the scrape
    // that depends on it, and the whole search verified nothing. Measured: with a shared 6s, the
    // single most inflated source in a result set (186 claimed, 1 real) never got measured at all.
    private static readonly TimeSpan ResolveBudget = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan ScrapeBudget = TimeSpan.FromSeconds(6);

    // Trackers queried concurrently. Three is enough to survive one being slow without turning a
    // search into a burst of UDP traffic.
    private const int TrackersQueried = 3;

    private const long ProtocolId = 0x41727101980;

    // The trailing negative lookahead matters: without it a longer token (a v2 hash, or corrupt
    // input) matches its first 40 characters, which can be valid hex and would then be scraped as if
    // it were a real infohash. Silently querying the wrong swarm is worse than not querying at all.
    private static readonly Regex BtihPattern = new(
        @"xt=urn:btih:([A-Za-z0-9]{32,40})(?![A-Za-z0-9])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // How many otherwise-unverifiable candidates are worth a round trip to learn their infohash.
    // Bounded because this runs on a user-facing search; the highest claims go first, since those are
    // both the most likely to be inflated and the most likely to be ranked into the viewer's face.
    private const int LinkResolveBudget = 6;

    private readonly TrackerList _trackers;
    private readonly System.Net.Http.IHttpClientFactory _httpClientFactory;
    private readonly Diagnostics.IEngineMetrics _metrics;
    private readonly ILogger<SwarmScraper> _logger;

    /// <summary>Initializes a new instance of the <see cref="SwarmScraper"/> class.</summary>
    /// <param name="trackers">Supplies the trackers to ask.</param>
    /// <param name="httpClientFactory">Used to resolve HTTP links that hide their infohash.</param>
    /// <param name="metrics">Records how often an indexer's claim turned out to be fiction.</param>
    /// <param name="logger">Logger.</param>
    public SwarmScraper(
        TrackerList trackers,
        System.Net.Http.IHttpClientFactory httpClientFactory,
        Diagnostics.IEngineMetrics metrics,
        ILogger<SwarmScraper> logger)
    {
        _trackers = trackers;
        _httpClientFactory = httpClientFactory;
        _metrics = metrics;
        _logger = logger;
    }

    /// <summary>
    /// Measures real swarm health and writes it back onto the candidates, in place.
    /// </summary>
    /// <param name="sources">Search results, mutated in place.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task VerifyAsync(IReadOnlyList<TorrentSource> sources, CancellationToken cancellationToken)
    {
        if (sources is null || sources.Count == 0)
        {
            return;
        }

        // Only magnets carry an infohash for free. An HTTP .torrent link would have to be fetched and
        // parsed first, which is far too expensive for twenty results on a user-facing search, so
        // those keep the indexer's claim and are reported unverified.
        var byHash = new Dictionary<string, List<TorrentSource>>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
        {
            // Prefer the infohash the indexer stated outright: it covers HTTP-proxy results that have
            // no magnet to parse, which is the majority of what Prowlarr returns and the group whose
            // claimed seeder counts are least trustworthy.
            var hex = Normalise(source.InfoHash) ?? InfoHashOf(source.DownloadUrl);
            if (hex is not null)
            {
                if (!byHash.TryGetValue(hex, out var list))
                {
                    byHash[hex] = list = new List<TorrentSource>();
                }

                list.Add(source);
            }
        }

        // Some indexers report neither a magnet nor an infohash — LimeTorrents is one, and its claims
        // were the least accurate measured (186 seeders against a real swarm of 1). Those would
        // otherwise be permanently unverifiable and would rank above genuinely healthy sources purely
        // on the strength of an invented number, so the worst offenders get one round trip to find
        // out. Often free: Prowlarr's download links usually 302 straight to a magnet, which carries
        // the infohash without downloading anything.
        var opaque = sources
            .Where(s => !byHash.Values.Any(g => g.Contains(s)))
            .Where(s => s.DownloadUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(s => s.Seeders)
            .Take(LinkResolveBudget)
            .ToList();

        if (opaque.Count > 0)
        {
            using var resolveCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            resolveCts.CancelAfter(ResolveBudget);

            var resolved = await Task.WhenAll(opaque.Select(async s =>
                (Source: s, Hash: await ResolveInfoHashAsync(s.DownloadUrl, resolveCts.Token).ConfigureAwait(false))))
                .ConfigureAwait(false);

            foreach (var (source, hash) in resolved.Where(r => r.Hash is not null))
            {
                if (!byHash.TryGetValue(hash!, out var list))
                {
                    byHash[hash!] = list = new List<TorrentSource>();
                }

                list.Add(source);
            }
        }

        if (byHash.Count == 0)
        {
            _logger.LogInformation("Orca Engine: no infohash could be determined; keeping indexer counts");
            return;
        }

        var trackers = (await _trackers.GetAsync(cancellationToken).ConfigureAwait(false))
            .Where(t => t.StartsWith("udp://", StringComparison.OrdinalIgnoreCase))
            .Take(TrackersQueried)
            .ToList();

        using var scrapeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        scrapeCts.CancelAfter(ScrapeBudget);

        var hashes = byHash.Keys.ToList();
        var results = await Task.WhenAll(trackers.Select(t => ScrapeTrackerAsync(t, hashes, scrapeCts.Token)))
            .ConfigureAwait(false);

        // Merge by best-known: a tracker that has never seen a torrent reports zero, which says
        // nothing about the swarm, so the maximum across trackers is the only honest reading.
        var merged = new Dictionary<string, (int Seeders, int Leechers)>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in results.Where(r => r is not null).SelectMany(r => r!))
        {
            if (!merged.TryGetValue(pair.Key, out var best) || pair.Value.Seeders > best.Seeders)
            {
                merged[pair.Key] = pair.Value;
            }
        }

        var verified = 0;
        var wereLies = 0;
        foreach (var (hex, group) in byHash)
        {
            if (!merged.TryGetValue(hex, out var actual))
            {
                continue;
            }

            foreach (var source in group)
            {
                if (source.Seeders > actual.Seeders + 1)
                {
                    wereLies++;
                }

                source.Seeders = actual.Seeders;
                source.Leechers = actual.Leechers;
                source.SwarmVerified = true;
                verified++;
            }
        }

        // Counted, not just logged: "how often do indexers lie about seeders" is the question this
        // whole class exists to answer, and a running total answers it better than any single search.
        _metrics.Increment("swarm.verify.checked", sources.Count);
        _metrics.Increment("swarm.verify.measured", verified);
        _metrics.Increment("swarm.verify.inflated", wereLies);

        _logger.LogInformation(
            "Orca Engine: swarm-verified {Verified}/{Total} sources via {Trackers} tracker(s); "
                + "{Lies} had an inflated seeder count",
            verified,
            sources.Count,
            trackers.Count,
            wereLies);
    }

    /// <summary>
    /// Extracts the v1 infohash from a magnet URI as lowercase hex.
    /// </summary>
    /// <param name="downloadUrl">A magnet URI, or an HTTP link (which yields null).</param>
    /// <returns>40-char lowercase hex, or null when there is no usable v1 infohash.</returns>
    /// <remarks>
    /// Magnets carry the hash as either 40-char hex or 32-char Base32, and both appear in the wild
    /// from real indexers. Internal so both encodings stay under test.
    /// </remarks>
    internal static string? InfoHashOf(string? downloadUrl)
    {
        if (string.IsNullOrWhiteSpace(downloadUrl)
            || !downloadUrl.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var match = BtihPattern.Match(downloadUrl);
        if (!match.Success)
        {
            return null;
        }

        var raw = match.Groups[1].Value;
        if (raw.Length == 40 && raw.All(Uri.IsHexDigit))
        {
            return raw.ToLowerInvariant();
        }

        if (raw.Length == 32)
        {
            var decoded = FromBase32(raw);
            return decoded is null ? null : Convert.ToHexString(decoded).ToLowerInvariant();
        }

        return null;
    }

    /// <summary>
    /// Learns the infohash behind an indexer's HTTP download link.
    /// </summary>
    /// <param name="url">The indexer/Prowlarr download URL.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Lowercase hex infohash, or null when it could not be determined cheaply.</returns>
    /// <remarks>
    /// Uses the redirect-disabled client, which is what makes the cheap path possible: Prowlarr's
    /// download links commonly 302 to a <c>magnet:</c> URI, and a magnet carries the infohash without
    /// transferring a torrent file at all. Only when the link really serves a .torrent do we parse
    /// one. Never throws — an unverifiable source simply keeps the indexer's claim.
    /// </remarks>
    private async Task<string?> ResolveInfoHashAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var client = _httpClientFactory.CreateClient(TorrentStreamService.TorrentFetchClient);
            client.Timeout = TimeSpan.FromSeconds(5);

            using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode is System.Net.HttpStatusCode.Redirect
                or System.Net.HttpStatusCode.Moved
                or System.Net.HttpStatusCode.RedirectMethod
                or System.Net.HttpStatusCode.TemporaryRedirect)
            {
                return InfoHashOf(response.Headers.Location?.ToString());
            }

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            var torrent = await MonoTorrent.Torrent.LoadAsync(bytes).ConfigureAwait(false);
            return torrent.InfoHashes.V1OrV2.ToHex().ToLowerInvariant();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Orca Engine: could not learn an infohash from {Url}", url);
            return null;
        }
    }

    /// <summary>
    /// Normalises a bare infohash string, as an indexer reports it, to lowercase hex.
    /// </summary>
    /// <param name="value">Hex or Base32 infohash, or null.</param>
    /// <returns>40-char lowercase hex, or null when it is not a usable v1 infohash.</returns>
    /// <remarks>
    /// Indexers are inconsistent: hex or Base32, upper or lower, occasionally an empty string or a
    /// v2 hash. Anything not a 20-byte v1 hash is rejected rather than guessed at, because scraping
    /// a wrong hash silently reports another torrent's swarm as this one's.
    /// </remarks>
    internal static string? Normalise(string? value)
    {
        var raw = value?.Trim();
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        if (raw.Length == 40 && raw.All(Uri.IsHexDigit))
        {
            return raw.ToLowerInvariant();
        }

        if (raw.Length == 32)
        {
            var decoded = FromBase32(raw);
            return decoded is null ? null : Convert.ToHexString(decoded).ToLowerInvariant();
        }

        return null;
    }

    /// <summary>Decodes RFC 4648 Base32 (no padding) into bytes, or null when malformed.</summary>
    /// <param name="value">The Base32 text.</param>
    /// <returns>The decoded bytes, or null.</returns>
    internal static byte[]? FromBase32(string value)
    {
        const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bits = 0;
        var acc = 0;
        var output = new List<byte>(value.Length * 5 / 8);

        foreach (var c in value.ToUpperInvariant())
        {
            var index = Alphabet.IndexOf(c, StringComparison.Ordinal);
            if (index < 0)
            {
                return null;
            }

            acc = (acc << 5) | index;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                output.Add((byte)(acc >> bits));
            }
        }

        return output.Count == 20 ? output.ToArray() : null;
    }

    /// <summary>Scrapes one tracker for every hash, returning null when it could not be reached.</summary>
    private async Task<Dictionary<string, (int Seeders, int Leechers)>?> ScrapeTrackerAsync(
        string trackerUrl,
        IReadOnlyList<string> hashes,
        CancellationToken cancellationToken)
    {
        try
        {
            var uri = new Uri(trackerUrl);
            using var udp = new UdpClient();
            udp.Connect(uri.Host, uri.Port);

            var connectionId = await ConnectAsync(udp, cancellationToken).ConfigureAwait(false);
            if (connectionId is null)
            {
                return null;
            }

            var found = new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase);
            foreach (var batch in hashes.Chunk(HashesPerScrape))
            {
                var scraped = await ScrapeBatchAsync(udp, connectionId.Value, batch, cancellationToken)
                    .ConfigureAwait(false);
                if (scraped is null)
                {
                    break;
                }

                foreach (var kv in scraped)
                {
                    found[kv.Key] = kv.Value;
                }
            }

            return found;
        }
        catch (Exception ex)
        {
            // One unreachable tracker is routine and must not disturb the search.
            _logger.LogDebug(ex, "Orca Engine: scrape failed against {Tracker}", trackerUrl);
            return null;
        }
    }

    private static async Task<long?> ConnectAsync(UdpClient udp, CancellationToken cancellationToken)
    {
        var transactionId = Random.Shared.Next();
        var request = new byte[16];
        BinaryPrimitives.WriteInt64BigEndian(request, ProtocolId);
        BinaryPrimitives.WriteInt32BigEndian(request.AsSpan(8), 0);
        BinaryPrimitives.WriteInt32BigEndian(request.AsSpan(12), transactionId);

        await udp.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var response = await udp.ReceiveAsync(cancellationToken).ConfigureAwait(false);
        var buffer = response.Buffer;

        if (buffer.Length < 16
            || BinaryPrimitives.ReadInt32BigEndian(buffer) != 0
            || BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan(4)) != transactionId)
        {
            return null;
        }

        return BinaryPrimitives.ReadInt64BigEndian(buffer.AsSpan(8));
    }

    private static async Task<Dictionary<string, (int, int)>?> ScrapeBatchAsync(
        UdpClient udp,
        long connectionId,
        IReadOnlyList<string> hashes,
        CancellationToken cancellationToken)
    {
        var transactionId = Random.Shared.Next();
        var request = new byte[16 + (hashes.Count * 20)];
        BinaryPrimitives.WriteInt64BigEndian(request, connectionId);
        BinaryPrimitives.WriteInt32BigEndian(request.AsSpan(8), 2);
        BinaryPrimitives.WriteInt32BigEndian(request.AsSpan(12), transactionId);
        for (var i = 0; i < hashes.Count; i++)
        {
            Convert.FromHexString(hashes[i]).CopyTo(request, 16 + (i * 20));
        }

        await udp.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var response = await udp.ReceiveAsync(cancellationToken).ConfigureAwait(false);
        var buffer = response.Buffer;

        if (buffer.Length < 8
            || BinaryPrimitives.ReadInt32BigEndian(buffer) != 2
            || BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan(4)) != transactionId)
        {
            return null;
        }

        // 12 bytes per hash, in request order: seeders, completed, leechers.
        var result = new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase);
        var available = (buffer.Length - 8) / 12;
        for (var i = 0; i < Math.Min(available, hashes.Count); i++)
        {
            var at = 8 + (i * 12);
            result[hashes[i]] = (
                BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan(at)),
                BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan(at + 8)));
        }

        return result;
    }
}
