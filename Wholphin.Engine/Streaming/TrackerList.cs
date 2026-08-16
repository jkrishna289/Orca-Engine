using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Wholphin.Engine.Streaming;

/// <summary>
/// Supplies the public trackers every streamable torrent is announced to.
/// </summary>
/// <remarks>
/// Peer discovery here is trackers and nothing else: MonoTorrent 3.0.2's DHT never bootstraps —
/// measured on two unrelated networks, it sends its queries and receives zero bytes back, and the
/// public <c>IDht</c> façade offers no way to inject working routers. So the quality of this list is
/// the quality of peer discovery.
///
/// A hardcoded list was measured once, on one host, and that turned out to be the wrong shape of
/// answer: tracker health is **host-specific and changes over time**. The same four trackers measured
/// dead from the server answered healthily from a dev machine on another network. So this tracks
/// ngosang/trackerslist, which is regenerated daily from live measurements, and falls back to the
/// hand-measured list when it cannot be fetched.
///
/// Deliberately the *best* list (20 entries), not <c>trackers_all</c> (hundreds). Every tracker costs
/// an announce, and a flood of them is the same connection churn that once exhausted the household
/// router's NAT table and took the whole LAN down.
/// </remarks>
public sealed class TrackerList
{
    /// <summary>The curated, daily-regenerated list. All-UDP, ~20 entries.</summary>
    public const string SourceUrl =
        "https://raw.githubusercontent.com/ngosang/trackerslist/master/trackers_best.txt";

    /// <summary>Hard ceiling on how many are used, so a bad upstream edit cannot flood announces.</summary>
    internal const int MaxTrackers = 30;

    private static readonly TimeSpan RefreshAfter = TimeSpan.FromHours(12);

    /// <summary>
    /// Used until the list is fetched, and whenever fetching fails. Measured against the live server
    /// with <c>scratchpad/tracker_health.py</c>: each answered a BEP-15 connect AND returned peers.
    /// </summary>
    internal static readonly string[] Fallback =
    [
        "udp://tracker.opentrackr.org:1337/announce",
        "udp://open.stealth.si:80/announce",
        "udp://open.demonii.com:1337/announce",
        "udp://tracker.dler.org:6969/announce",
    ];

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TrackerList> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string[] _current = Fallback;
    private DateTimeOffset _fetchedAt = DateTimeOffset.MinValue;

    /// <summary>Initializes a new instance of the <see cref="TrackerList"/> class.</summary>
    /// <param name="httpClientFactory">Jellyfin's HTTP client factory.</param>
    /// <param name="logger">Logger.</param>
    public TrackerList(IHttpClientFactory httpClientFactory, ILogger<TrackerList> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Gets the trackers to announce to, refreshing from upstream at most twice a day.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Announce URLs. Never empty — falls back rather than returning nothing.</returns>
    /// <remarks>
    /// Never throws and never blocks a stream on the network: a failed refresh keeps whatever is
    /// already held, because opening a stream with a slightly stale tracker list is always better
    /// than not opening one at all.
    /// </remarks>
    public async Task<IReadOnlyList<string>> GetAsync(CancellationToken cancellationToken)
    {
        if (DateTimeOffset.UtcNow - _fetchedAt < RefreshAfter)
        {
            return _current;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Another caller may have refreshed while this one waited.
            if (DateTimeOffset.UtcNow - _fetchedAt < RefreshAfter)
            {
                return _current;
            }

            using var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            var body = await client.GetStringAsync(SourceUrl, cancellationToken).ConfigureAwait(false);

            var parsed = Parse(body);
            if (parsed.Length == 0)
            {
                _logger.LogWarning("Orca stream: tracker list fetched but held no usable entries; keeping previous");
                return _current;
            }

            _current = parsed;
            _fetchedAt = DateTimeOffset.UtcNow;
            _logger.LogInformation("Orca stream: refreshed public tracker list — {Count} trackers", parsed.Length);
            return _current;
        }
        catch (Exception ex)
        {
            // Back off for the full interval rather than retrying on every session, so an upstream
            // outage costs one failed request per 12h instead of one per stream.
            _fetchedAt = DateTimeOffset.UtcNow;
            _logger.LogWarning(
                ex,
                "Orca stream: could not refresh the tracker list; using {Count} known-good trackers",
                _current.Length);
            return _current;
        }
    }

    /// <summary>
    /// Turns the upstream file into announce URLs.
    /// </summary>
    /// <param name="body">Raw file contents.</param>
    /// <returns>Usable, de-duplicated announce URLs, capped at <see cref="MaxTrackers"/>.</returns>
    /// <remarks>
    /// Internal so the parsing is unit-tested: the file is blank-line separated, may gain comments,
    /// and sibling lists carry <c>ws://</c> WebTorrent entries that MonoTorrent cannot use and would
    /// only fail on. Anything not udp/http/https is dropped rather than trusted.
    /// </remarks>
    internal static string[] Parse(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return [];
        }

        return body
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .Where(line =>
                line.StartsWith("udp://", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            .Where(line => Uri.TryCreate(line, UriKind.Absolute, out _))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxTrackers)
            .ToArray();
    }
}
