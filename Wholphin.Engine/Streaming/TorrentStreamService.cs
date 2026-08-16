using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Net;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;
using Wholphin.Engine.Diagnostics;
using MonoTorrent;
using MonoTorrent.Client;
using MonoTorrent.Connections;

namespace Wholphin.Engine.Streaming;

/// <summary>How far along a session is. A session exists from the instant it is asked for.</summary>
public enum StreamSessionState
{
    /// <summary>Peers, metadata or the head/tail pieces are still arriving. No deadline applies.</summary>
    Preparing,

    /// <summary>The file is open and readable — playback can start.</summary>
    Ready,

    /// <summary>Preparation failed outright (no playable video, or the swarm errored).</summary>
    Failed,
}

/// <summary>A live torrent stream the HTTP layer can read from.</summary>
public sealed class StreamSession
{
    /// <summary>Gets the content id (torrent infohash). Used to share one session between viewers.</summary>
    public required string Id { get; init; }

    /// <summary>
    /// Gets how far along preparation is.
    /// </summary>
    /// <remarks>
    /// A session is handed to the caller while still <see cref="StreamSessionState.Preparing"/>. There
    /// is deliberately no timeout on reaching <see cref="StreamSessionState.Ready"/>: a popular torrent
    /// can take minutes to find its first reachable peer, and cutting it off at an arbitrary second
    /// mark told viewers a healthy swarm was dead. The viewer decides when to give up, not a constant.
    /// </remarks>
    public StreamSessionState State { get; internal set; } = StreamSessionState.Preparing;

    /// <summary>Gets why preparation failed, when it did. Null while preparing or once ready.</summary>
    public string? FailureReason { get; internal set; }

    /// <summary>
    /// Gets the unguessable token that appears in the stream URL. Deliberately not the infohash:
    /// the infohash is public, and the playback URL has to work in a plain player that cannot
    /// attach an auth header, so the URL itself is the capability.
    /// </summary>
    public required string Token { get; init; }

    /// <summary>Gets the selected file's name (used for the Content-Type guess). Empty until ready.</summary>
    public string FileName { get; internal set; } = string.Empty;

    /// <summary>Gets the selected file's total length in bytes. Zero until ready.</summary>
    public long Length { get; internal set; }

    /// <summary>
    /// Gets the probed track list, or null when ffprobe was unavailable or couldn't read the partial
    /// file. Null is a supported outcome: the client then lets the player discover tracks itself.
    /// </summary>
    public StreamMediaInfo? MediaInfo { get; internal set; }

    /// <summary>Gets the time of the most recent read, used for idle eviction.</summary>
    public DateTimeOffset LastAccess { get; internal set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Gets a value indicating whether the viewer asked to keep this title.
    /// </summary>
    /// <remarks>
    /// A kept session is exempt from idle eviction: it has to outlive playback by however long the
    /// remaining pieces take, which is usually far longer than the idle window. It is the one reason
    /// a session stays alive with nobody reading it.
    /// </remarks>
    public bool Kept { get; internal set; }

    /// <summary>Gets when the session was asked for — the instant the viewer pressed play.</summary>
    internal DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

    /// <summary>Gets when preparation finished, or null while still preparing.</summary>
    internal DateTimeOffset? ReadyAt { get; set; }

    /// <summary>Gets when the player took its first byte, or null if it never has.</summary>
    internal DateTimeOffset? FirstReadAt { get; set; }

    /// <summary>
    /// Gets bytes handed to the player, as opposed to bytes pulled from the swarm.
    /// </summary>
    /// <remarks>
    /// Deliberately not <see cref="StreamSessionHealth.DownloadedBytes"/>. That counts what came off
    /// the wire, and the two diverge precisely when playback is going well: a piece already in the
    /// cache is delivered without downloading anything. Only this number answers whether the player
    /// is being fed fast enough to keep rendering.
    /// </remarks>
    internal long BytesDelivered;

    /// <summary>Gets reads that blocked longer than the stall threshold. Reset each heartbeat.</summary>
    internal int Stalls;

    /// <summary>Gets the longest single blocked read, in ms. Reset each heartbeat.</summary>
    internal long WorstStallMs;

    // Lifetime totals, never reset. The heartbeat's counters answer "how is it going right now";
    // these answer "how did this session go", which is the question the dashboard is asked.
    internal int TotalStalls;
    internal long WorstStallEverMs;

    /// <summary>Gets when the last heartbeat was logged, so the next one can measure a real interval.</summary>
    internal DateTimeOffset? HeartbeatAt { get; set; }

    /// <summary>Gets <see cref="BytesDelivered"/> as of the last heartbeat, to difference against.</summary>
    internal long HeartbeatBytes { get; set; }

    /// <summary>Gets the file chosen for playback — needed to find it on disk once complete.</summary>
    internal ITorrentManagerFile? File { get; set; }

    // Not `required`: C# forbids a required member less visible than its containing type, and these
    // are internal on purpose — callers outside the streaming layer have no business touching the
    // torrent handle.
    //
    // Null until preparation adds the torrent, which now happens after metadata has been fetched
    // rather than at session creation — so everything reading this must cope with its absence.
    internal TorrentManager? Manager { get; set; }

    internal Stream? Stream { get; set; }

    /// <summary>
    /// Cancelled when the session is torn down, so the metadata fetch — which has no deadline of its
    /// own, by design — cannot outlive the session that wanted it.
    /// </summary>
    internal CancellationTokenSource Cancellation { get; } = new();

    // MonoTorrent allows exactly one live stream per StreamProvider ("this stream must be disposed
    // before another stream can be created"), so every read seeks the same handle and must be
    // serialized.
    // ponytail: one lock per session serializes all reads; only matters if a client opens
    // overlapping ranges — split into a stream pool if that shows up in practice.
    internal SemaphoreSlim Gate { get; } = new(1, 1);

    /// <summary>
    /// Reads live swarm health straight off the torrent handle.
    /// </summary>
    /// <remarks>
    /// Every number here is measured, never estimated. The client prints them during the wait, so an
    /// invented value would be a visible lie — a viewer watching "0 peers" climb to "14 peers" learns
    /// something true about why they are waiting, which is the entire point of showing it.
    /// </remarks>
    public StreamSessionHealth ReadHealth()
    {
        var manager = Manager;
        if (manager is null)
        {
            // No torrent yet: the metadata fetch that has to precede it is still running. "Metadata"
            // is exactly what MonoTorrent would report at this point anyway, and it is the state the
            // client already words as "reading file list", so the wait reads correctly either way.
            return new StreamSessionHealth { TorrentState = "Metadata" };
        }

        var monitor = manager.Monitor;
        return new StreamSessionHealth
        {
            Peers = manager.Peers?.Available ?? 0,
            OpenConnections = manager.OpenConnections,
            Seeds = manager.Peers?.Seeds ?? 0,
            Leechers = manager.Peers?.Leechs ?? 0,
            DownloadRateBytesPerSecond = monitor?.DownloadRate ?? 0,
            UploadRateBytesPerSecond = monitor?.UploadRate ?? 0,
            DownloadedBytes = monitor?.DataBytesReceived ?? 0,
            Progress = manager.Progress,
            TorrentState = manager.State.ToString(),
            HasMetadata = manager.HasMetadata,
            Diagnostics = SwarmDiagnostics.Describe(manager),
        };
    }
}

/// <summary>
/// Reads the parts of MonoTorrent that explain *why* a swarm is empty — which tracker was asked,
/// whether it answered, and whether DHT ever bootstrapped.
/// </summary>
/// <remarks>
/// By reflection on purpose. These are not part of MonoTorrent's documented surface, and a
/// diagnostic must never be why the build breaks on a library upgrade — every lookup degrades to a
/// marker string rather than throwing. It is surfaced on the status endpoint as well as the log so
/// diagnosing a dead swarm does not require root on the server to read journalctl.
/// </remarks>
internal static class SwarmDiagnostics
{
    /// <summary>Follows a property path, returning a marker instead of throwing at any step.</summary>
    /// <param name="target">Root object.</param>
    /// <param name="path">Property names to walk.</param>
    /// <returns>The value's string form, or a marker describing where it gave up.</returns>
    internal static string Read(object? target, params string[] path)
    {
        foreach (var name in path)
        {
            if (target is null)
            {
                return "null";
            }

            var prop = target.GetType().GetProperty(name);
            if (prop is null)
            {
                return $"?{name}";
            }

            try
            {
                target = prop.GetValue(target);
            }
            catch (Exception ex)
            {
                return $"!{ex.GetType().Name}";
            }
        }

        return target?.ToString() ?? "null";
    }

    /// <summary>Describes a torrent's tracker and DHT state in one line.</summary>
    /// <param name="manager">The torrent.</param>
    /// <returns>A compact human-readable summary.</returns>
    internal static string Describe(TorrentManager manager)
    {
        try
        {
            var trackers = new List<string>();
            if (manager.TrackerManager?.Tiers is { } tiers)
            {
                foreach (var tier in tiers)
                {
                    if (tier.GetType().GetProperty("Trackers")?.GetValue(tier) is System.Collections.IEnumerable list)
                    {
                        foreach (var tracker in list)
                        {
                            trackers.Add($"{Read(tracker, "Uri")}=[{Read(tracker, "Status")}]");
                        }
                    }
                }
            }

            // Every counter on PeerManager, not just Available: "announce Ok but avail=0" could mean
            // the peers were never stored, or that they were stored and immediately moved somewhere
            // else (connecting, banned, busy). Those need very different fixes, and only the full set
            // tells them apart.
            var peerCounts = "?";
            try
            {
                var peers = manager.Peers;
                if (peers is not null)
                {
                    peerCounts = string.Join(
                        ",",
                        peers.GetType()
                            .GetProperties()
                            .Where(p => p.PropertyType == typeof(int) && p.GetIndexParameters().Length == 0)
                            .Select(p => $"{p.Name}={Read(peers, p.Name)}"));
                }
            }
            catch (Exception ex)
            {
                peerCounts = $"!{ex.GetType().Name}";
            }

            return $"dht={Read(manager, "Engine", "Dht", "State")} "
                + $"open={Read(manager, "OpenConnections")} "
                + $"peers[{peerCounts}] "
                + $"tiers={manager.TrackerManager?.Tiers?.Count ?? -1} "
                + $"trackers={(trackers.Count == 0 ? "NONE" : string.Join(" ", trackers))}";
        }
        catch (Exception ex)
        {
            return $"diagnostics failed: {ex.GetType().Name}";
        }
    }
}

/// <summary>A measured snapshot of one session's swarm, taken at request time.</summary>
public sealed class StreamSessionHealth
{
    /// <summary>Gets the number of peers currently known and reachable.</summary>
    public int Peers { get; init; }

    /// <summary>
    /// Gets how many peers there is an actual connection to right now.
    /// </summary>
    /// <remarks>
    /// The number that separates "this swarm is thin" from "we are barely talking to it". A session
    /// can see 56 seeds and be connected to eight of them, and only this tells the two apart —
    /// <see cref="Peers"/> counts what the trackers reported, not what is being downloaded from.
    /// </remarks>
    public int OpenConnections { get; init; }

    /// <summary>Gets how many of those peers hold the complete file.</summary>
    public int Seeds { get; init; }

    /// <summary>Gets how many are still downloading it themselves.</summary>
    public int Leechers { get; init; }

    /// <summary>Gets the current inbound rate in bytes per second.</summary>
    public long DownloadRateBytesPerSecond { get; init; }

    /// <summary>Gets the current outbound rate in bytes per second.</summary>
    public long UploadRateBytesPerSecond { get; init; }

    /// <summary>Gets total bytes pulled from the swarm so far this session.</summary>
    public long DownloadedBytes { get; init; }

    /// <summary>Gets completion of the whole torrent, 0-100.</summary>
    public double Progress { get; init; }

    /// <summary>Gets MonoTorrent's own state name (Metadata, Hashing, Downloading, Seeding…).</summary>
    public string TorrentState { get; init; } = string.Empty;

    /// <summary>Gets whether the file list has arrived yet — false means the magnet is still resolving.</summary>
    public bool HasMetadata { get; init; }

    /// <summary>Gets the tracker/DHT detail behind an empty swarm. Diagnostic only, never shown to a viewer.</summary>
    public string Diagnostics { get; init; } = string.Empty;
}

/// <summary>
/// Whether peers on the internet can actually open a connection to this server.
/// </summary>
/// <remarks>
/// Deliberately separate from the router's port-mapping status, which is a far weaker claim: a
/// mapping being <c>Created</c> only means the router said yes, not that anything traversed it. ISP
/// CGNAT, a second router, a firewall rule or a mapping to the wrong internal host all leave the
/// mapping looking perfect while every inbound packet is dropped.
///
/// So the only value that means "proven" is <see cref="Reachable"/>, and it is issued on one piece
/// of evidence and no other: a peer connection whose direction was inbound. Nothing else can be
/// faked by wishful reading of local state.
/// </remarks>
public enum InboundReachability
{
    /// <summary>No listener yet, so there is nothing to be reachable. Not a claim either way.</summary>
    Unknown,

    /// <summary>The listener failed to bind, so nothing can arrive regardless of the router.</summary>
    NotReachable,

    /// <summary>Bound and mapped, but no peer has yet arrived inbound. Absence of proof, not proof of absence.</summary>
    Pending,

    /// <summary>Proven: at least one peer opened a connection to us.</summary>
    Reachable,
}

/// <summary>Whether the router is actually forwarding the peer port, as far as can be shown.</summary>
/// <remarks>
/// Four states rather than a boolean, because "is port forwarding working" has three honest answers
/// and only one of them is yes. A router reporting a created mapping is not the same as a packet
/// arriving through it, and collapsing the two would tell an operator their networking is fine while
/// every inbound connection is being dropped upstream.
/// </remarks>
public enum PortForwardState
{
    /// <summary>Not requested — UPnP is switched off in configuration.</summary>
    Off,

    /// <summary>Requested, but the router refused or never answered. Manual forwarding is the way out.</summary>
    NotWorking,

    /// <summary>The router created a mapping, but nothing has yet arrived through it.</summary>
    Mapped,

    /// <summary>Proven: a peer connected in, so traffic really does traverse the mapping.</summary>
    Working,
}

/// <summary>
/// How reachable this server is to peers — the difference between chasing a swarm and being found by
/// one.
/// </summary>
/// <remarks>
/// Exists because "is the port actually forwarded" was unanswerable without root on the box. An
/// inbound peer costs no outbound connection attempt, so this is the one place where connectivity can
/// improve without spending any of the router's NAT budget; it deserves to be readable.
///
/// Configured and effective values are reported separately throughout. They diverge in exactly the
/// cases worth knowing about — a port already in use, a save that never reached the engine — and a
/// dashboard that showed only the stored setting would report the operator's intention back at them
/// and call it status.
/// </remarks>
public sealed class StreamConnectivity
{
    /// <summary>Gets the port configuration asks for. Zero means "any", which is not forwardable.</summary>
    public int ConfiguredListenPort { get; init; }

    /// <summary>
    /// Gets the port actually bound, read from the live listener. Zero when it could not be read.
    /// </summary>
    /// <remarks>
    /// The number that matters. When configuration says 51413 and this says something else — or
    /// nothing — the setting did not take, and every other reading on the page is about a port no
    /// peer is being told about.
    /// </remarks>
    public int ActualListenPort { get; init; }

    /// <summary>Gets a value indicating whether a listener is actually bound and accepting.</summary>
    public bool ListenerBound { get; init; }

    /// <summary>Gets the evidence-based reachability verdict.</summary>
    public string Reachability { get; init; } = nameof(InboundReachability.Unknown);

    /// <summary>Gets how many peers have connected *to* us — the proof behind a Reachable verdict.</summary>
    public int InboundConnections { get; init; }

    /// <summary>Gets when the most recent inbound peer arrived, or null if none ever has.</summary>
    public DateTimeOffset? LastInboundAt { get; init; }

    /// <summary>Gets how many peers we opened connections to, for contrast with the inbound count.</summary>
    public int OutboundConnections { get; init; }

    /// <summary>Gets the externally advertised endpoint, when one is configured. Empty otherwise.</summary>
    public string ReportedEndPoint { get; init; } = string.Empty;

    /// <summary>Gets a value indicating whether the port is fixed by configuration rather than random per restart.</summary>
    public bool PortIsFixed { get; init; }

    /// <summary>Gets a value indicating whether the router was asked to forward the port.</summary>
    public bool PortForwardingRequested { get; init; }

    /// <summary>Gets router mappings that succeeded, as "protocol public→private".</summary>
    public IReadOnlyList<string> MappingsCreated { get; init; } = [];

    /// <summary>Gets mappings still being negotiated.</summary>
    public IReadOnlyList<string> MappingsPending { get; init; } = [];

    /// <summary>Gets mappings the router refused — usually the public port already being in use.</summary>
    public IReadOnlyList<string> MappingsFailed { get; init; } = [];

    /// <summary>Gets DHT's own state name. Never reaching Ready is a known MonoTorrent 3.0.2 defect here.</summary>
    public string DhtState { get; init; } = string.Empty;

    /// <summary>Gets a value indicating whether DHT is switched on in configuration.</summary>
    public bool DhtEnabled { get; init; }

    /// <summary>Gets how many nodes DHT currently knows. Zero with traffic flowing means bootstrap is failing.</summary>
    public int DhtNodes { get; init; }

    /// <summary>Gets bytes DHT has sent — queries going out.</summary>
    public long DhtBytesSent { get; init; }

    /// <summary>
    /// Gets bytes DHT has received.
    /// </summary>
    /// <remarks>
    /// The pair with <see cref="DhtBytesSent"/> is what separates the two very different failures that
    /// both present as "no peers from DHT": nothing sent means DHT never started, while plenty sent
    /// and nothing received means the queries are leaving and the answers never arrive — which is the
    /// shape of the never-bootstraps behaviour measured on this host.
    /// </remarks>
    public long DhtBytesReceived { get; init; }

    /// <summary>Gets whether port forwarding is off, failed, mapped-but-unproven, or proven working.</summary>
    public string PortForwarding { get; init; } = nameof(PortForwardState.Off);

    /// <summary>Gets a value indicating whether Peer Exchange is switched on.</summary>
    public bool PeerExchangeEnabled { get; init; }

    /// <summary>Gets a value indicating whether Local Peer Discovery is switched on.</summary>
    public bool LocalPeerDiscoveryEnabled { get; init; }

    /// <summary>Gets the encryption types offered to peers, in preference order.</summary>
    public IReadOnlyList<string> Encryption { get; init; } = [];

    /// <summary>Gets the connection ceiling currently applied.</summary>
    public int MaxConnections { get; init; }

    /// <summary>Gets the in-flight connection-attempt ceiling currently applied.</summary>
    public int MaxHalfOpenConnections { get; init; }
}

/// <summary>Owns torrent streaming sessions: creation, reads, and teardown.</summary>
public interface ITorrentStreamService
{
    /// <summary>Starts (or returns) a session streaming the best video file in a torrent.</summary>
    /// <param name="source">
    /// Either a <c>magnet:</c> URI or an HTTP link to a .torrent file. Public indexers surface both,
    /// and Prowlarr in particular usually gives a .torrent proxy link rather than a magnet.
    /// </param>
    /// <param name="season">Target season for a season pack, if known.</param>
    /// <param name="episode">Target episode for a season pack, if known.</param>
    /// <param name="fromPublicIndexer">
    /// True when the source came from a search whose indexers were privacy-filtered, which is what
    /// makes it safe to announce this infohash to public trackers. False for a caller-supplied magnet,
    /// where we cannot know the torrent's origin until its metadata arrives.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The session, or null when the torrent is unusable or holds no video.</returns>
    Task<StreamSession?> StartAsync(
        string source,
        int? season,
        int? episode,
        bool fromPublicIndexer,
        CancellationToken cancellationToken);

    /// <summary>Looks up a live session by its URL token.</summary>
    /// <param name="token">The capability token from the stream URL.</param>
    /// <returns>The session, or null when unknown/evicted.</returns>
    StreamSession? Get(string token);

    /// <summary>Every session the service currently holds, for the operator dashboard.</summary>
    /// <returns>A snapshot of the live sessions.</returns>
    /// <remarks>
    /// Deliberately not <see cref="Get"/> in a loop: this must NOT refresh LastAccess, or an admin
    /// leaving the Observatory open on this tab would keep abandoned sessions alive forever by
    /// watching them.
    /// </remarks>
    IReadOnlyList<StreamSession> Sessions();

    /// <summary>Reports how the engine is reachable: listen port, router mapping and DHT state.</summary>
    /// <returns>The connectivity snapshot, or null when no engine has been created yet.</returns>
    StreamConnectivity? Connectivity();

    /// <summary>Measures the piece cache on disk: bytes held, files, and the budget it is held under.</summary>
    /// <returns>Bytes used, file count and the configured budget in bytes.</returns>
    (long UsedBytes, int Files, long BudgetBytes) CacheUsage();

    /// <summary>
    /// Brings peer discovery up before anything needs it: listener bound, port mapped, DHT bootstrapping.
    /// </summary>
    /// <returns>A task.</returns>
    /// <remarks>
    /// Called at plugin startup and kept up thereafter, because none of this is instant and all of it
    /// used to begin at the moment a viewer pressed play. DHT in particular needs minutes to find its
    /// first nodes, which is why it was logged as <c>Initialising</c> partway through a stream and
    /// <c>NotReady</c> at the end of one.
    /// </remarks>
    Task WarmUpAsync();

    /// <summary>Reads from a session at an absolute file offset.</summary>
    /// <param name="session">The session.</param>
    /// <param name="offset">Absolute offset within the file.</param>
    /// <param name="buffer">Destination buffer.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Bytes read; 0 at end of file.</returns>
    Task<int> ReadAsync(StreamSession session, long offset, Memory<byte> buffer, CancellationToken cancellationToken);

    /// <summary>Tears a session down and stops its torrent.</summary>
    /// <param name="token">The session's URL token.</param>
    /// <returns>A task.</returns>
    Task StopAsync(string token);

    /// <summary>
    /// Keeps a streamed title: finishes the download, copies it into the library folder and asks
    /// Jellyfin to scan it in.
    /// </summary>
    /// <param name="token">The session's URL token.</param>
    /// <returns>True when the session exists and keeping has started; false when it is gone.</returns>
    /// <remarks>
    /// Returns as soon as the work is scheduled, not when it finishes. Completing a torrent takes as
    /// long as it takes, and the viewer has already walked away from the screen that asked.
    /// </remarks>
    Task<bool> KeepAsync(string token);
}

/// <summary>
/// MonoTorrent-backed streaming. One <see cref="ClientEngine"/> for the process, one session per
/// magnet, each exposing a seekable stream that blocks until the pieces under the read position
/// arrive — which is what lets an ordinary HTTP Range request drive piece priority.
///
/// Everything here is bounded on purpose. MonoTorrent runs in-process inside Jellyfin, so an
/// unbounded session count would put the media server at the mercy of a bad source.
/// </summary>
public sealed class TorrentStreamService : ITorrentStreamService, IDisposable
{
    private readonly IApplicationPaths _appPaths;
    private readonly ILogger<TorrentStreamService> _logger;
    private readonly MediaProbe _probe;
    private readonly System.Net.Http.IHttpClientFactory _httpClientFactory;
    private readonly MediaBrowser.Controller.Library.ILibraryManager _libraryManager;
    private readonly TrackerList _trackers;
    private readonly ConcurrentDictionary<string, StreamSession> _sessions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly Timer _idleSweep;

    private ClientEngine? _engine;
    private bool _disposed;

    // Signature of the settings currently applied to _engine, so a configuration change can be
    // detected without asking Jellyfin to tell us about one.
    private string _appliedSettings = string.Empty;

    // Peer connections observed by direction. An inbound one is the ONLY evidence that survives
    // scrutiny for "the internet can reach this server" — a router mapping proves the router agreed,
    // nothing more. Counted for the life of the process, since reachability is a property of the
    // network rather than of any one stream.
    private int _inboundPeers;
    private int _outboundPeers;
    private DateTimeOffset? _lastInboundAt;

    // Managers already subscribed to, so reusing one for a second session doesn't double-count.
    private readonly HashSet<InfoHashes> _peerWatched = [];

    // Wall-clock ceiling for a probe. Generous enough for a slow-but-alive source to deliver 13MB,
    // short enough that a dead one doesn't leave the viewer staring at a spinner.
    private const int ProbeBudgetSeconds = 90;

    // How long a single read may block before it is worth a warning. A player's buffer absorbs a
    // hitch of a second or two without the viewer seeing anything; past three, frames are at risk.
    private const int StallWarnMs = 3000;

    // How long a session goes unread before its torrent is paused.
    //
    // Streaming downloads ahead of the play head, and it does not stop when the viewer does: after
    // playback ended the torrent kept pulling ~1 MB/s across 40 peers for the full 20-minute idle
    // window, saturating the household connection for a film nobody was watching. Pausing is not
    // eviction — the session, its peers and its cached pieces all survive, so resuming is immediate.
    //
    // Two minutes, because a player with a deep buffer legitimately stops reading for a while and
    // must not be paused mid-film. The heartbeat's own interval is one minute, so this is the first
    // threshold that cannot be tripped by a single quiet tick.
    private const int PauseAfterMinutes = 2;

    // Peer limits. These are NOT download-speed knobs — they are a budget against the consumer router
    // in front of the server, whose NAT table a torrent can exhaust, at which point it drops
    // connections indiscriminately, including the LAN HTTP the player streams over. That is not
    // theoretical: 200 conns / 20 half-open / 5s timeout killed all connectivity ~40s into a stream.
    //
    // The budget is arithmetic, not superstition. Measured on this host,
    // nf_conntrack_tcp_timeout_syn_sent = 120s, so an unanswered SYN occupies a NAT slot for two
    // minutes — twelve times our own 10s give-up. What loads the router is therefore the *rate* of
    // new attempts, not how many are in flight: lingering entries ≈ (half-open ÷ timeout) × 120.
    //
    //     8 / 10s = 0.8/s ≈  96 entries   (this setting — the only one observed to be safe)
    //    20 / 10s = 2.0/s ≈ 240 entries   (tried 2026-08-13, see below)
    //    20 /  5s = 4.0/s ≈ 480 entries   (the August config that took the LAN down)
    //
    // **20 was tried and reverted.** The arithmetic above says it should have been about half the
    // known-bad load, and it still coincided with the LAN dropping: an SSH session to this box was
    // reset mid-test, not merely the torrent poll, which is the same whole-network signature as
    // August. It recovered on its own, as August did.
    //
    // That is one observation, not proof — but the cost of being wrong is every device in the house
    // losing connectivity, so it does not get the benefit of the doubt. What the model evidently
    // misses is that lingering SYNs are not the only NAT entries: established peer connections and
    // whatever the household is doing share the same table, and the router's real ceiling is
    // unknown and unmeasurable from here.
    //
    // ponytail: do not raise this again on arithmetic alone. The lever that adds peers WITHOUT
    // adding outbound NAT entries is inbound reachability (AllowPortForwarding); prove that one in
    // isolation first.
    //
    // These are now StreamMaxConnections / StreamMaxHalfOpenConnections / StreamConnectionTimeoutSeconds
    // in PluginConfiguration, defaulting to the 80 / 8 / 10s above. They became editable so an operator
    // can tune them against their own router; the reason not to is this comment, which is why it stays
    // here rather than being reduced to help text on a form.

    // Public trackers now come from TrackerList (ngosang/trackerslist, refreshed daily) rather than
    // a constant here — see that class for why a hardcoded list was the wrong shape of answer.

    /// <summary>Named HTTP client used to fetch .torrent files, configured not to follow redirects.</summary>
    public const string TorrentFetchClient = "orca-torrent-fetch";

    /// <summary>Initializes a new instance of the <see cref="TorrentStreamService"/> class.</summary>
    /// <param name="appPaths">Jellyfin application paths (for the default cache directory).</param>
    /// <param name="logger">Logger.</param>
    /// <param name="probe">Container prober.</param>
    /// <param name="httpClientFactory">HTTP client factory, for fetching .torrent files.</param>
    /// <param name="libraryManager">Jellyfin's library manager, used to scan in a kept title.</param>
    /// <param name="trackers">Supplies the public trackers to announce to.</param>
    public TorrentStreamService(
        IApplicationPaths appPaths,
        ILogger<TorrentStreamService> logger,
        MediaProbe probe,
        System.Net.Http.IHttpClientFactory httpClientFactory,
        MediaBrowser.Controller.Library.ILibraryManager libraryManager,
        TrackerList trackers)
    {
        _appPaths = appPaths;
        _logger = logger;
        _probe = probe;
        _httpClientFactory = httpClientFactory;
        _libraryManager = libraryManager;
        _trackers = trackers;
        // Keep-warm rides the sweep's timer but deliberately NOT SweepIdle itself: that method is also
        // called from inside the session-start gate, where a warm-up's UPnP round trip would sit in
        // front of a viewer waiting to play. Fire-and-forget for the same reason — the timer thread
        // must not block on the network. WarmUpAsync handles its own exceptions.
        _idleSweep = new Timer(
            _ =>
            {
                SweepIdle();

                // MonoTorrent stops the engine once the last torrent is removed — which the sweep may
                // just have caused — so this is what puts DHT and the listener back up between
                // streams instead of letting the next viewer pay to start them again.
                _ = WarmUpAsync();
            },
            null,
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(1));
    }

    // Same accessor shape the rest of the engine uses (see AdminController) — config is read live
    // so an admin toggling limits doesn't need a restart.
    private static Configuration.PluginConfiguration Config =>
        Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();

    private string CacheDir
    {
        get
        {
            var configured = Config.StreamCachePath;
            return string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(_appPaths.DataPath, "wholphin-engine", "streams")
                : configured;
        }
    }

    /// <summary>
    /// Where a kept title is copied to. Empty disables keeping entirely — there is no sensible
    /// default here, because guessing a folder Jellyfin does not actually watch would report success
    /// and produce nothing.
    /// </summary>
    private static string LibraryDir => Config.StreamLibraryPath ?? string.Empty;

    /// <inheritdoc />
    public async Task WarmUpAsync()
    {
        if (!Config.FeatureSourceStreaming)
        {
            // Nothing is warmed while the feature is off — no listener, no DHT traffic, no router
            // mapping. Switching it on is what starts any of that, and the sweep picks it up within
            // a minute of the setting being saved.
            return;
        }

        try
        {
            var engine = GetOrCreateEngine();
            await ApplySettingsAsync(engine).ConfigureAwait(false);

            if (engine.IsRunning)
            {
                return;
            }

            // Reflection, and worth explaining. ClientEngine.StartAsync is internal and is normally
            // reached only by starting a torrent — which is exactly the coupling being removed here.
            // It is what starts port forwarding, Local Peer Discovery, the DHT engine (loading its
            // cached node list), and the peer listeners, and it self-guards on IsRunning, so calling
            // it is idempotent.
            //
            // Doing this at startup rather than at play time is the whole point: DHT needs minutes to
            // bootstrap and a UPnP mapping needs a round trip to the router, and previously both began
            // when a viewer pressed play. Measured 2026-08-16: dht=Initialising at t+50s of a stream
            // and still NotReady at t+60s, having been created seconds earlier by that same stream.
            //
            // Fails soft: if MonoTorrent renames or reworks this, warm-up quietly does nothing and the
            // old lazy behaviour — engine starts with the first torrent — is exactly what remains.
            var start = engine.GetType().GetMethod(
                "StartAsync",
                System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Public,
                binder: null,
                types: Type.EmptyTypes,
                modifiers: null);

            if (start?.Invoke(engine, null) is Task task)
            {
                await task.ConfigureAwait(false);
                var (port, bound) = ActualListener(engine);
                _logger.LogInformation(
                    "Orca stream: peer discovery warmed up — listener {State}{Port}, dht={Dht}, "
                        + "lsd={Lsd}, forwarding={Forwarding}",
                    bound ? "bound on" : "not bound",
                    bound ? $" {port}" : string.Empty,
                    engine.Dht?.State.ToString() ?? "off",
                    engine.Settings.AllowLocalPeerDiscovery,
                    engine.Settings.AllowPortForwarding);
            }
            else
            {
                _logger.LogInformation(
                    "Orca stream: could not pre-start peer discovery; it will come up with the first stream");
            }
        }
        catch (Exception ex)
        {
            // Never fatal. Warm-up is an optimisation; the lazy path still works.
            _logger.LogWarning(ex, "Orca stream: peer-discovery warm-up failed; falling back to starting on demand");
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<StreamSession> Sessions() => _sessions.Values.ToList();

    /// <summary>
    /// Counts peer connections by direction, which is what makes inbound reachability provable.
    /// </summary>
    /// <param name="manager">The torrent to watch.</param>
    /// <remarks>
    /// Subscribed once per infohash: a second session on the same torrent reuses the manager, and
    /// subscribing again would double every count and make an unreachable server look busy.
    /// </remarks>
    private void WatchPeers(TorrentManager manager)
    {
        lock (_peerWatched)
        {
            if (!_peerWatched.Add(manager.InfoHashes))
            {
                return;
            }
        }

        manager.PeerConnected += (_, e) =>
        {
            if (e.Direction == Direction.Incoming)
            {
                Interlocked.Increment(ref _inboundPeers);
                _lastInboundAt = DateTimeOffset.UtcNow;

                // Logged at Information because it is the single most significant connectivity event
                // this server can record: it is the moment "can peers reach us" stops being a guess.
                _logger.LogInformation(
                    "Orca stream: inbound peer connection — this server is reachable from outside "
                        + "({Count} inbound so far)",
                    _inboundPeers);
            }
            else
            {
                Interlocked.Increment(ref _outboundPeers);
            }
        };
    }

    /// <summary>
    /// Reads the port the listener actually bound, as opposed to the one configuration asked for.
    /// </summary>
    /// <param name="engine">The live engine.</param>
    /// <returns>The bound port, or 0 when it could not be read.</returns>
    /// <remarks>
    /// By reflection, because <c>ClientEngine.PeerListeners</c> is internal — the same deliberate
    /// trade-off <see cref="SwarmDiagnostics"/> documents, and for the same reason: this is a
    /// diagnostic, and a diagnostic must never be why the build breaks on a library upgrade. Any
    /// failure degrades to 0, which the page reports as "unknown" rather than as a port.
    ///
    /// Worth the reflection because <c>LocalEndPoint</c> is null until a listener has genuinely bound.
    /// That distinction — asked for versus actually listening — is exactly what a port already in use
    /// looks like, and there is no public API that reveals it.
    /// </remarks>
    private static (int Port, bool Bound) ActualListener(ClientEngine engine)
    {
        try
        {
            var listeners = engine.GetType()
                .GetProperty("PeerListeners", System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.NonPublic)
                ?.GetValue(engine) as System.Collections.IEnumerable;

            if (listeners is null)
            {
                return (0, false);
            }

            foreach (var listener in listeners)
            {
                var endPoint = listener?.GetType()
                    .GetProperty("LocalEndPoint")
                    ?.GetValue(listener) as IPEndPoint;

                if (endPoint is not null && endPoint.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    return (endPoint.Port, true);
                }
            }

            return (0, false);
        }
        catch
        {
            return (0, false);
        }
    }

    /// <summary>
    /// Decides what can honestly be claimed about inbound reachability.
    /// </summary>
    /// <param name="listenerBound">Whether a listener is actually accepting connections.</param>
    /// <param name="inboundPeers">How many peers have connected to us.</param>
    /// <param name="engineExists">Whether an engine has been created at all.</param>
    /// <returns>The verdict.</returns>
    /// <remarks>
    /// Only an inbound connection promotes this to <see cref="InboundReachability.Reachable"/>. A
    /// created port mapping deliberately does NOT: CGNAT, a second router, a firewall rule, or a
    /// mapping pointed at the wrong internal host all leave the mapping looking perfect while every
    /// packet is dropped. Reporting those as reachable would send an operator hunting the wrong fault.
    /// </remarks>
    /// <summary>
    /// Decides what can honestly be claimed about the router forwarding the peer port.
    /// </summary>
    /// <param name="requested">Whether UPnP forwarding is switched on.</param>
    /// <param name="created">Whether the router reported a mapping as created.</param>
    /// <param name="pending">Whether a mapping is still being negotiated.</param>
    /// <param name="inboundPeers">Peers that have connected to us.</param>
    /// <returns>The state.</returns>
    /// <remarks>
    /// Only an inbound peer promotes this past <see cref="PortForwardState.Mapped"/>. The gap between
    /// "mapped" and "working" is not pedantry — CGNAT, a second router upstream, a firewall rule, or a
    /// mapping aimed at the wrong internal host each leave a perfectly created mapping carrying
    /// nothing, and each is invisible from this side of it.
    /// </remarks>
    internal static PortForwardState ForwardState(bool requested, bool created, bool pending, int inboundPeers)
    {
        if (!requested)
        {
            return PortForwardState.Off;
        }

        if (inboundPeers > 0)
        {
            return PortForwardState.Working;
        }

        if (created)
        {
            return PortForwardState.Mapped;
        }

        // Still negotiating is not yet a failure; the router simply has not answered.
        return pending ? PortForwardState.Mapped : PortForwardState.NotWorking;
    }

    internal static InboundReachability Verdict(bool listenerBound, int inboundPeers, bool engineExists)
    {
        if (!engineExists)
        {
            return InboundReachability.Unknown;
        }

        if (inboundPeers > 0)
        {
            return InboundReachability.Reachable;
        }

        // Bound but nobody has arrived: the swarm may simply not have dialled us yet, which is
        // ordinary in the first minute of a session. That is not evidence of unreachability.
        return listenerBound ? InboundReachability.Pending : InboundReachability.NotReachable;
    }

    /// <inheritdoc />
    public StreamConnectivity? Connectivity()
    {
        var engine = _engine;
        if (engine is null)
        {
            // No stream has been opened since startup, so there is no engine and nothing is listening.
            // Reporting the configured intent here would claim a port that is not actually bound.
            return null;
        }

        var config = Config;
        var mappings = engine.PortMappings;
        var (actualPort, bound) = ActualListener(engine);

        static IReadOnlyList<string> Describe(IReadOnlyList<MonoTorrent.PortForwarding.Mapping> source)
            => source.Select(m => $"{m.Protocol} {m.PublicPort}→{m.PrivatePort}").ToList();

        return new StreamConnectivity
        {
            // Configured and effective are reported separately on purpose. They diverge when a port
            // is already in use, and that is precisely the case a single "listen port" number would
            // hide behind the operator's own setting.
            ConfiguredListenPort = engine.Settings.ListenEndPoints.TryGetValue("ipv4", out var listen) ? listen.Port : 0,
            ActualListenPort = actualPort,
            ListenerBound = bound,
            Reachability = Verdict(bound, _inboundPeers, engineExists: true).ToString(),
            InboundConnections = _inboundPeers,
            OutboundConnections = _outboundPeers,
            LastInboundAt = _lastInboundAt,
            ReportedEndPoint = engine.Settings.ReportedListenEndPoints.TryGetValue("ipv4", out var reported)
                ? reported.ToString()
                : string.Empty,
            PortIsFixed = ClampPort(config.StreamListenPort) > 0,
            PortForwardingRequested = engine.Settings.AllowPortForwarding,
            MappingsCreated = Describe(mappings.Created),
            MappingsPending = Describe(mappings.Pending),
            MappingsFailed = Describe(mappings.Failed),
            DhtState = engine.Dht?.State.ToString() ?? "Disabled",
            DhtEnabled = config.StreamEnableDht,
            DhtNodes = engine.Dht?.NodeCount ?? 0,
            DhtBytesSent = engine.Dht?.Monitor?.BytesSent ?? 0,
            DhtBytesReceived = engine.Dht?.Monitor?.BytesReceived ?? 0,
            PortForwarding = ForwardState(
                engine.Settings.AllowPortForwarding,
                mappings.Created.Count > 0,
                mappings.Pending.Count > 0,
                _inboundPeers).ToString(),
            PeerExchangeEnabled = config.StreamEnablePeerExchange,
            LocalPeerDiscoveryEnabled = engine.Settings.AllowLocalPeerDiscovery,
            Encryption = engine.Settings.AllowedEncryption.Select(e => e.ToString()).ToList(),
            MaxConnections = engine.Settings.MaximumConnections,
            MaxHalfOpenConnections = engine.Settings.MaximumHalfOpenConnections,
        };
    }

    /// <inheritdoc />
    public (long UsedBytes, int Files, long BudgetBytes) CacheUsage()
    {
        var budget = (long)Math.Max(1, Config.StreamCacheMaxGb) * 1024 * 1024 * 1024;
        var dir = CacheDir;
        if (!Directory.Exists(dir))
        {
            return (0, 0, budget);
        }

        // Same enumeration TrimCache evicts from, so the tile and the trim can never disagree about
        // what is on disk.
        var files = new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).ToList();
        return (files.Sum(f => f.Length), files.Count, budget);
    }

    /// <inheritdoc />
    public StreamSession? Get(string token)
    {
        if (_sessions.TryGetValue(token, out var session))
        {
            session.LastAccess = DateTimeOffset.UtcNow;
            return session;
        }

        return null;
    }

    /// <inheritdoc />
    public async Task<StreamSession?> StartAsync(
        string source,
        int? season,
        int? episode,
        bool fromPublicIndexer,
        CancellationToken cancellationToken)
    {
        // Resolve to something MonoTorrent can add. A magnet carries its infohash inline; a .torrent
        // link has to be fetched and parsed before we know what it even is.
        var resolved = await ResolveAsync(source, fromPublicIndexer, cancellationToken).ConfigureAwait(false);
        if (resolved is null)
        {
            return null;
        }

        StreamSession? created;

        // The infohash identifies the content, so two viewers asking for the same source share one
        // session and one set of peers rather than downloading it twice.
        var id = resolved.InfoHash;
        var existing = FindByContentId(id);
        if (existing is not null)
        {
            existing.LastAccess = DateTimeOffset.UtcNow;
            return existing;
        }

        // Serialized so two simultaneous requests for a new magnet can't both build a session, and
        // so the concurrency cap is actually a cap.
        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            existing = FindByContentId(id);
            if (existing is not null)
            {
                existing.LastAccess = DateTimeOffset.UtcNow;
                return existing;
            }

            SweepIdle();
            if (_sessions.Count >= Math.Max(1, Config.MaxConcurrentStreamSessions))
            {
                _logger.LogWarning(
                    "Orca stream: refusing new session, {Count} already running (MaxConcurrentStreamSessions)",
                    _sessions.Count);
                return null;
            }

            created = CreateSession(id, resolved, season, episode);
        }
        finally
        {
            _startGate.Release();
        }

        // Probing moved into PrepareSessionAsync along with everything else slow: it pulls ~13MB
        // through the torrent, and there is nothing to probe until the file has been picked anyway.

        return created;
    }

    /// <summary>
    /// Probes a session's container under a hard wall-clock bound. Reads block until pieces arrive, so
    /// without this a stalled torrent would hang the session request indefinitely. A timeout is not an
    /// error: the client falls back to letting the player discover tracks itself.
    /// </summary>
    private async Task<StreamMediaInfo?> ProbeSafelyAsync(StreamSession session, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(ProbeBudgetSeconds));

        try
        {
            return await _probe
                .ProbeAsync(
                    session.Length,
                    (offset, buffer, ct) => ReadAsync(session, offset, buffer, ct),
                    CacheDir,
                    cts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation(
                "Orca stream: probe for {Id} exceeded {Seconds}s — playing without track metadata",
                session.Id,
                ProbeBudgetSeconds);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<int> ReadAsync(
        StreamSession session,
        long offset,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        if (offset >= session.Length)
        {
            return 0;
        }

        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // No stream until preparation finishes. The HTTP layer refuses reads before Ready, so this
            // is belt-and-braces — but it is the difference between an early request answering 0 bytes
            // and one throwing a NullReferenceException at the player.
            var stream = session.Stream;
            if (stream is null)
            {
                return 0;
            }

            // Touch before the read as well as after. A read legitimately blocks while the pieces
            // under the play head arrive — sometimes for minutes on a slow source — and marking the
            // session live only on completion would let the idle sweep evict it mid-read.
            session.LastAccess = DateTimeOffset.UtcNow;

            // A viewer came back. Resume before reading, or the read would block forever against a
            // torrent that has stopped asking anyone for pieces.
            if (session.Manager is { State: TorrentState.Paused } paused)
            {
                _logger.LogInformation("Orca stream: resuming {Id} — a reader returned", session.Id);
                await paused.StartAsync().ConfigureAwait(false);
            }

            if (stream.Position != offset)
            {
                // Seeking re-points MonoTorrent's sequential picker, so a viewer jumping ahead
                // starts pulling pieces from the new position instead of finishing the old run.
                stream.Seek(offset, SeekOrigin.Begin);
            }

            // Every player read funnels through here, so this is the only place that has to be timed.
            // Nothing else records the read path at all: Jellyfin logs no HTTP requests, so without
            // this a read that blocked eight seconds and one that returned instantly are both silent.
            var startedAt = Environment.TickCount64;
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            var blockedMs = Environment.TickCount64 - startedAt;

            session.LastAccess = DateTimeOffset.UtcNow;

            // Only the player's reads count as delivery. ffprobe comes through this same funnel while
            // the session is still Preparing — it reads 12MB of head and then the tail — so counting
            // it would both steal the first-frame line and inflate the first heartbeat's rate with
            // bytes no viewer ever saw. Stalls below stay unconditional: a probe read that blocked 16
            // seconds is the best explanation there is for why the wait was long.
            if (session.State == StreamSessionState.Ready)
            {
                Interlocked.Add(ref session.BytesDelivered, read);

                if (session.FirstReadAt is null)
                {
                    session.FirstReadAt = session.LastAccess;

                    // Time-to-first-frame, as the viewer experiences it: this plus the existing
                    // "fetching metadata" -> "session ready" pair spans the whole press-play wait.
                    _logger.LogInformation(
                        "Orca stream: session {Id} first frame — {Ms}ms after ready, {Bytes} bytes at offset {Offset}",
                        session.Id,
                        session.ReadyAt is { } readyAt ? (long)(session.LastAccess - readyAt).TotalMilliseconds : -1,
                        read,
                        offset);
                }
            }

            if (blockedMs >= StallWarnMs)
            {
                Interlocked.Increment(ref session.Stalls);
                Interlocked.Increment(ref session.TotalStalls);
                if (blockedMs > Interlocked.Read(ref session.WorstStallMs))
                {
                    Interlocked.Exchange(ref session.WorstStallMs, blockedMs);
                }

                if (blockedMs > Interlocked.Read(ref session.WorstStallEverMs))
                {
                    Interlocked.Exchange(ref session.WorstStallEverMs, blockedMs);
                }

                _logger.LogWarning(
                    "Orca stream: session {Id} read at offset {Offset} blocked {Ms}ms ({Rate} B/s from the swarm)",
                    session.Id,
                    offset,
                    blockedMs,
                    session.ReadHealth().DownloadRateBytesPerSecond);
            }

            return read;
        }
        finally
        {
            session.Gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task StopAsync(string token)
    {
        if (!_sessions.TryRemove(token, out var session))
        {
            return;
        }

        await TearDownAsync(session).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<bool> KeepAsync(string token)
    {
        var session = Get(token);
        if (session is null)
        {
            return Task.FromResult(false);
        }

        if (string.IsNullOrWhiteSpace(LibraryDir))
        {
            _logger.LogWarning("Orca stream: asked to keep {Id} but no library folder is configured", session.Id);
            return Task.FromResult(false);
        }

        // Idempotent: the prompt can only be answered once, but a retried request must not start a
        // second completion task against the same torrent.
        if (session.Kept)
        {
            return Task.FromResult(true);
        }

        session.Kept = true;
        _ = Task.Run(() => CompleteAndImportAsync(session), CancellationToken.None);
        return Task.FromResult(true);
    }

    /// <summary>
    /// Runs a kept session to completion, then copies the file into the library folder and asks
    /// Jellyfin to pick it up.
    /// </summary>
    /// <remarks>
    /// Nothing here touches <see cref="StreamSession.Stream"/>. The torrent downloads every piece on
    /// its own once it is left running — the stream cursor only decides the *order* pieces are asked
    /// for, never whether they arrive — so polling progress keeps this off the single shared reader
    /// and out of the way of anyone still watching.
    /// </remarks>
    private async Task CompleteAndImportAsync(StreamSession session)
    {
        var manager = session.Manager;
        if (manager is null)
        {
            // Keep is only offered after a film has played, which cannot happen before the torrent
            // was added — but the session could have been torn down in between.
            _logger.LogInformation("Orca stream: keep for {Id} abandoned — no torrent on the session", session.Id);
            return;
        }

        try
        {
            // ponytail: polls every 15s rather than hooking MonoTorrent's completion event. A copy
            // that starts up to 15s late costs nothing on work measured in minutes, and the poll also
            // covers the case where the torrent was already complete when the viewer said yes.
            while (!manager.Complete)
            {
                if (!_sessions.ContainsKey(session.Token))
                {
                    _logger.LogInformation("Orca stream: keep for {Id} abandoned — session gone", session.Id);
                    return;
                }

                // Keeps the session's own idle sweep exemption honest, and stops a long completion
                // from looking abandoned in the logs.
                session.LastAccess = DateTimeOffset.UtcNow;
                await Task.Delay(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            }

            var file = session.File;
            var sourcePath = file?.DownloadCompleteFullPath;
            if (string.IsNullOrWhiteSpace(sourcePath) || !System.IO.File.Exists(sourcePath))
            {
                _logger.LogWarning(
                    "Orca stream: keep for {Id} finished downloading but {Path} is not on disk",
                    session.Id,
                    sourcePath);
                return;
            }

            Directory.CreateDirectory(LibraryDir);
            var destination = Path.Combine(LibraryDir, session.FileName);

            // Copied, not moved: the torrent is still seeding from these bytes, and moving the file
            // out from under MonoTorrent breaks the session for anyone still watching it.
            if (System.IO.File.Exists(destination))
            {
                _logger.LogInformation("Orca stream: {File} is already in the library folder", session.FileName);
            }
            else
            {
                // Copy to a temp name first, then rename. A library scan that catches a half-copied
                // file imports a truncated item, and Jellyfin will happily keep that forever.
                var staging = destination + ".orca-partial";
                await CopyFileAsync(sourcePath, staging).ConfigureAwait(false);
                System.IO.File.Move(staging, destination);

                _logger.LogInformation(
                    "Orca stream: kept {File} ({Bytes} bytes) into {Dir}",
                    session.FileName,
                    session.Length,
                    LibraryDir);
            }

            _libraryManager.QueueLibraryScan();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Orca stream: keeping {Id} failed", session.Id);
        }
        finally
        {
            // Whether it worked or not, the session stops being exempt from the idle sweep — leaving
            // it pinned would hold a slot and a swarm forever on a failure nobody is watching.
            session.Kept = false;
        }
    }

    private static async Task CopyFileAsync(string source, string destination)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        await input.CopyToAsync(output).ConfigureAwait(false);
    }

    // Sessions are keyed by URL token, so content dedup is a scan — bounded by
    // MaxConcurrentStreamSessions (default 3), which is cheaper than a second index to keep in sync.
    private StreamSession? FindByContentId(string contentId) =>
        _sessions.Values.FirstOrDefault(s => string.Equals(s.Id, contentId, StringComparison.Ordinal));

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _idleSweep.Dispose();

        foreach (var session in _sessions.Values.ToList())
        {
            TearDownAsync(session).GetAwaiter().GetResult();
        }

        _sessions.Clear();
        _engine?.Dispose();
        _startGate.Dispose();
    }

    /// <summary>
    /// A torrent reduced to what the session needs: its infohash, plus whichever form MonoTorrent can
    /// add it from. Exactly one of [Magnet] or [Torrent] is set.
    /// </summary>
    private sealed record ResolvedTorrent(string InfoHash, MagnetLink? Magnet, Torrent? Torrent);

    /// <summary>
    /// Appends the measured-healthy public trackers to a magnet URI before it is ever used.
    /// </summary>
    /// <param name="magnetUri">The original magnet.</param>
    /// <param name="fromPublicIndexer">Only public-indexer sources get this treatment — see remarks.</param>
    /// <returns>The magnet, with extra <c>tr=</c> parameters when eligible.</returns>
    /// <remarks>
    /// **This has to happen here, not later.** `SeedTrackersAsync` runs on the TorrentManager, which
    /// only exists *after* metadata has been fetched — and fetching metadata is precisely the stage
    /// that stalls. `DownloadMetadataAsync` can use only the trackers carried in the magnet itself
    /// plus DHT, and DHT is permanently dead here (see <see cref="PublicTrackers"/>). So a magnet
    /// whose own trackers have gone stale had no way to find a single peer, which is exactly what
    /// "0 seeders forever" was: two different Sintel sources sat in `Metadata` for 180s with zero
    /// peers while opentrackr was returning 50 peers for the same content.
    ///
    /// Gated on <paramref name="fromPublicIndexer"/> because announcing is not free of consequence:
    /// publishing a private tracker's infohash to public trackers exposes that swarm and can get the
    /// operator banned. Search results are already privacy-filtered (fail-closed) before they reach
    /// here, so those are safe. A caller-supplied magnet is not — its origin is unknowable until the
    /// metadata we do not yet have arrives — so it is left exactly as given.
    /// </remarks>
    private static string WithHealthyTrackers(
        string magnetUri,
        IReadOnlyList<string> trackers,
        bool fromPublicIndexer)
    {
        if (!fromPublicIndexer)
        {
            return magnetUri;
        }

        var builder = new System.Text.StringBuilder(magnetUri);
        foreach (var tracker in trackers)
        {
            // Skip ones the magnet already names, so a tier isn't padded with duplicates.
            if (magnetUri.Contains(Uri.EscapeDataString(tracker), StringComparison.OrdinalIgnoreCase)
                || magnetUri.Contains(tracker, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            builder.Append("&tr=").Append(Uri.EscapeDataString(tracker));
        }

        return builder.ToString();
    }

    /// <summary>
    /// Turns a magnet URI or a .torrent HTTP link into something addable.
    ///
    /// The HTTP path matters more than it looks: Prowlarr hands out .torrent proxy links far more often
    /// than magnets, so treating a magnet as the only acceptable input made most real search results
    /// unplayable.
    /// </summary>
    private async Task<ResolvedTorrent?> ResolveAsync(
        string source,
        bool fromPublicIndexer,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return null;
        }

        var trackers = await _trackers.GetAsync(cancellationToken).ConfigureAwait(false);

        if (source.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            if (!MagnetLink.TryParse(WithHealthyTrackers(source, trackers, fromPublicIndexer), out var link))
            {
                _logger.LogWarning("Orca stream: unparseable magnet link");
                return null;
            }

            _logger.LogInformation("Orca stream: resolved via direct magnet link");
            return new ResolvedTorrent(link.InfoHashes.V1OrV2.ToHex(), link, null);
        }

        if (!source.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Orca stream: source is neither a magnet nor an HTTP link");
            return null;
        }

        try
        {
            using var client = _httpClientFactory.CreateClient(TorrentFetchClient);
            client.Timeout = TimeSpan.FromSeconds(30);

            // Indexers commonly answer a .torrent request with a redirect to a magnet. This client is
            // registered with AllowAutoRedirect=false precisely so the 302 lands here instead of
            // HttpClient throwing on an unfollowable magnet: scheme.
            using var response = await client.GetAsync(source, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is System.Net.HttpStatusCode.Redirect
                or System.Net.HttpStatusCode.Moved
                or System.Net.HttpStatusCode.RedirectMethod
                or System.Net.HttpStatusCode.TemporaryRedirect)
            {
                var location = response.Headers.Location?.ToString();
                if (location?.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) == true
                    && MagnetLink.TryParse(WithHealthyTrackers(location, trackers, fromPublicIndexer), out var redirected))
                {
                    _logger.LogInformation("Orca stream: resolved via indexer redirect to a magnet");
                    return new ResolvedTorrent(redirected.InfoHashes.V1OrV2.ToHex(), redirected, null);
                }
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Orca stream: fetching the torrent file returned {Status}", (int)response.StatusCode);
                return null;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            var torrent = await Torrent.LoadAsync(bytes).ConfigureAwait(false);
            _logger.LogInformation("Orca stream: resolved via fetched .torrent file ({Bytes} bytes)", bytes.Length);
            return new ResolvedTorrent(torrent.InfoHashes.V1OrV2.ToHex(), null, torrent);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Orca stream: could not resolve the torrent source");
            return null;
        }
    }

    // Synchronous now: adding the torrent moved into PrepareSessionAsync, because a magnet has to be
    // resolved to metadata first and that is exactly the slow work this must not block on.
    private StreamSession? CreateSession(
        string id,
        ResolvedTorrent resolved,
        int? season,
        int? episode)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);

            // The session exists immediately and is returned in the Preparing state. Everything slow —
            // fetching metadata, adding the torrent, file selection, head/tail prebuffer — moves to the
            // background task below, so the HTTP request never blocks on the swarm and there is no
            // deadline to expire. The client polls ReadHealth() and decides for itself when to stop
            // waiting, which is the whole point: an unreachable swarm and a slow-but-alive one look
            // identical for the first minute, and only the viewer can say which is worth their time.
            var session = new StreamSession
            {
                Id = id,
                Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
            };

            _sessions[session.Token] = session;
            _ = Task.Run(() => PrepareSessionAsync(session, resolved, season, episode), CancellationToken.None);
            return session;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Orca stream: failed to start session for {Id}", id);
            return null;
        }
    }

    /// <summary>
    /// Turns a magnet into a fully-formed <see cref="Torrent"/> before it is ever handed to a
    /// <see cref="TorrentManager"/>.
    /// </summary>
    /// <remarks>
    /// This exists because of a hard MonoTorrent 3.0.2 defect: a TorrentManager created from a magnet
    /// never connects to a single peer. Its tracker announce reports Ok and its peer list stays empty
    /// forever — measured at open=0 avail=0 across repeated 2-minute runs, on two unrelated networks,
    /// with both our settings and stock defaults. In the same process, on the same magnet,
    /// <see cref="ClientEngine.DownloadMetadataAsync"/> pulls the metadata in seconds, and adding the
    /// resulting Torrent reaches 4 peers and 96 KB/s within ten seconds.
    ///
    /// So the magnet never reaches TorrentManager. Prowlarr returns magnets for nearly every source,
    /// which is why every stream was stuck at "finding people sharing this" regardless of how healthy
    /// the swarm actually was.
    /// </remarks>
    private async Task<Torrent> FetchMetadataAsync(
        ClientEngine engine,
        MagnetLink magnet,
        string id,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Orca stream: fetching metadata for {Id} before adding the torrent", id);
        var raw = await engine.DownloadMetadataAsync(magnet, cancellationToken).ConfigureAwait(false);

        try
        {
            return await Torrent.LoadAsync(raw.ToArray()).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Depending on the peer it came from this can be the bare 'info' dictionary rather than a
            // complete .torrent, which Torrent.LoadAsync rejects. Wrapping it costs one allocation and
            // saves a session that has already done the expensive part.
            _logger.LogDebug(ex, "Orca stream: metadata for {Id} needs the info-dict wrapper", id);
            var info = MonoTorrent.BEncoding.BEncodedValue.Decode(raw.ToArray());
            return Torrent.Load(new MonoTorrent.BEncoding.BEncodedDictionary { { "info", info } });
        }
    }

    /// <summary>
    /// Brings a started session up to playable, however long that takes.
    /// </summary>
    /// <remarks>
    /// Runs detached with <see cref="CancellationToken.None"/> on purpose: it must outlive the HTTP
    /// request that created it. Failure here is a real failure (no video in the torrent, or the engine
    /// erroring) — never "it was slow", because slowness no longer ends anything.
    /// </remarks>
    private async Task PrepareSessionAsync(
        StreamSession session,
        ResolvedTorrent resolved,
        int? season,
        int? episode)
    {
        var cancellationToken = session.Cancellation.Token;
        try
        {
            var engine = GetOrCreateEngine();

            // Pick up any configuration change before the swarm is touched, so a port or privacy
            // setting saved in the Observatory takes effect on the very next stream rather than at
            // the next Jellyfin restart.
            await ApplySettingsAsync(engine).ConfigureAwait(false);

            // A magnet must be resolved to full metadata BEFORE a TorrentManager sees it — see
            // FetchMetadataAsync for the MonoTorrent defect that forces this. A .torrent link already
            // carries its metadata, so it skips straight through.
            var torrent = resolved.Torrent
                ?? await FetchMetadataAsync(engine, resolved.Magnet!, session.Id, cancellationToken).ConfigureAwait(false);

            // Never stream a private torrent. BEP-27's private flag is set by the tracker that packed
            // the torrent, so this is authoritative — it does not depend on the indexer telling us the
            // truth, and it catches a private release no matter which route it arrived by.
            //
            // This is not a licensing nicety. Streaming reads scattered pieces and stops the moment the
            // viewer does, which on a private tracker is hit-and-run: it wrecks ratio and gets the
            // operator's account banned. The engine must refuse rather than leave that to whoever is
            // holding the remote.
            if (torrent.IsPrivate)
            {
                _logger.LogWarning(
                    "Orca stream: refusing private torrent {Name} ({Id}) — private trackers are never streamed",
                    torrent.Name,
                    session.Id);
                await FailSessionAsync(
                    session,
                    "This release is from a private tracker and can't be streamed.").ConfigureAwait(false);
                return;
            }

            // Reuse a manager the engine already holds for this infohash rather than adding a second.
            // MonoTorrent rejects a duplicate add, and a teardown that failed — or simply hasn't
            // finished its RemoveAsync — leaves the old manager registered. Without this, stopping a
            // stream and starting the same one again fails outright, which is exactly what a viewer
            // does after a source stalls.
            // DHT and Peer Exchange are per-torrent in MonoTorrent, so they have to be supplied at add
            // time rather than on the engine. Both find peers the trackers did not know about, and
            // neither costs an outbound connection attempt of its own.
            var torrentSettings = new TorrentSettingsBuilder
            {
                AllowDht = Config.StreamEnableDht,
                AllowPeerExchange = Config.StreamEnablePeerExchange,
            }.ToSettings();

            var manager = engine.Torrents.FirstOrDefault(t => t.InfoHashes == torrent.InfoHashes)
                ?? await engine.AddStreamingAsync(torrent, CacheDir, torrentSettings).ConfigureAwait(false);
            session.Manager = manager;
            WatchPeers(manager);

            await SeedTrackersAsync(manager, cancellationToken).ConfigureAwait(false);

            // A reused manager is already running; starting a started torrent throws.
            if (manager.State is TorrentState.Stopped or TorrentState.Stopping or TorrentState.Error)
            {
                await manager.StartAsync().ConfigureAwait(false);
            }

            _ = Task.Run(() => LogSwarmDiagnosticsAsync(session.Id, manager), CancellationToken.None);

            var entries = manager.Files
                .Select(f => new TorrentFileEntry(f.Path, f.Length))
                .ToList();

            var picked = VideoFileSelector.Select(entries, season, episode);
            if (picked is null)
            {
                _logger.LogWarning("Orca stream: no playable video in torrent {Id}", session.Id);
                await FailSessionAsync(session, "No playable video in this torrent.").ConfigureAwait(false);
                return;
            }

            var file = manager.Files.First(f => string.Equals(f.Path, picked.Path, StringComparison.Ordinal));

            // prebuffer: true fetches the first AND last piece before returning. The last piece
            // matters more than it looks — MKV cues and MP4 moov atoms often live at the tail, and
            // without them a player can open the file but refuse to seek.
            var stream = await manager.StreamProvider!
                .CreateStreamAsync(file, true, cancellationToken)
                .ConfigureAwait(false);

            session.FileName = Path.GetFileName(picked.Path);
            session.Length = picked.Length;
            session.Stream = stream;
            session.File = file;
            session.LastAccess = DateTimeOffset.UtcNow;

            // Probe BEFORE going Ready, never alongside playback.
            //
            // There is exactly one Stream per session (MonoTorrent: "this stream must be disposed
            // before another stream can be created"), so every reader shares one cursor. The probe
            // reads 12MB of head and then seeks to the tail; if the player is reading at the same
            // time, the two alternate under the Gate and each read re-seeks the cursor, which
            // re-points the sequential picker. Pieces then arrive for whichever reader seeked last
            // and the play head starves — download rate looks healthy, playback dies a few seconds
            // in, once the prebuffered head runs out. Ordering them in time is the fix: the player
            // is the only reader from Ready onwards.
            //
            // This costs nothing that wasn't going to be spent anyway. The probe's head and tail are
            // precisely what the player reads first, so by the time it starts those pieces are local
            // and playback opens with a 12MB head start instead of a fight.
            session.MediaInfo = await ProbeSafelyAsync(session, cancellationToken).ConfigureAwait(false);

            session.LastAccess = DateTimeOffset.UtcNow;
            session.ReadyAt = session.LastAccess;
            session.State = StreamSessionState.Ready;

            // The bitrate is what makes the heartbeat readable: delivery has to beat it for playback
            // to sustain, and it is already probed, so carrying it into the log costs nothing.
            _logger.LogInformation(
                "Orca stream: session {Id} ready — {File} ({Bytes} bytes, {Bitrate} bps)",
                session.Id,
                session.FileName,
                session.Length,
                session.MediaInfo?.Bitrate);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Orca stream: preparing session for {Id} failed", session.Id);
            await FailSessionAsync(session, "This stream could not be started.").ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Marks a session failed and frees its slot immediately.
    /// </summary>
    /// <remarks>
    /// Dropping it from the map right away is what stops a handful of dead swarms from occupying the
    /// concurrency cap for the full idle window — which reads, from the client, exactly like every
    /// subsequent source being dead too.
    /// </remarks>
    private async Task FailSessionAsync(StreamSession session, string reason)
    {
        session.State = StreamSessionState.Failed;
        session.FailureReason = reason;
        _sessions.TryRemove(session.Token, out _);
        session.Cancellation.Cancel();

        // The one case where the data goes too: a failed session has nothing playable to offer a
        // later attempt, so keeping its partial pieces would just crowd the cache budget.
        if (session.Manager is { } manager)
        {
            await TearDownManagerAsync(manager, deleteData: true).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reports what the swarm side is actually doing for the first minute of a session.
    /// </summary>
    /// <remarks>
    /// Exists because "0 peers" was indistinguishable from a dozen different faults. A plain BEP-15
    /// announce from this same host returns 25 peers for a torrent the engine finds none of, so the
    /// gap is between MonoTorrent and the trackers — and nothing in the status DTO could show which
    /// tracker was asked, whether it answered, or whether DHT ever bootstrapped.
    ///
    /// Reflection, deliberately: this reads MonoTorrent internals that are not part of its documented
    /// surface, and a diagnostic must never be the reason the build breaks on a library upgrade.
    /// Every lookup degrades to "?" instead of throwing.
    /// </remarks>
    private async Task LogSwarmDiagnosticsAsync(string id, TorrentManager manager)
    {
        // One forced announce, reported in full. The passive tracker Status only says "Ok" — it does
        // not say how many peers came back, which is the exact number in question when a successful
        // announce leaves the peer list empty.
        await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        _logger.LogInformation("Orca swarm {Id}: forced announce -> {Result}", id, await ForceAnnounceAsync(manager).ConfigureAwait(false));

        for (var round = 1; round <= 6; round++)
        {
            await Task.Delay(TimeSpan.FromSeconds(10)).ConfigureAwait(false);

            _logger.LogInformation(
                "Orca swarm {Id} t+{Sec}s: state={State} meta={Meta} {Detail}",
                id,
                round * 10,
                manager.State,
                manager.HasMetadata,
                SwarmDiagnostics.Describe(manager));
        }
    }

    /// <summary>
    /// Invokes MonoTorrent's own announce and reports what it returned.
    /// </summary>
    /// <remarks>
    /// Reflective because the announce entry point is not part of MonoTorrent's documented surface,
    /// and a diagnostic must not be able to break the build. Purely observational: announcing again
    /// is what the tracker client would do on its own interval anyway.
    /// </remarks>
    private static async Task<string> ForceAnnounceAsync(TorrentManager manager)
    {
        try
        {
            var trackerManager = manager.TrackerManager;
            if (trackerManager is null)
            {
                return "no TrackerManager";
            }

            var method = trackerManager.GetType()
                .GetMethods()
                .FirstOrDefault(m => m.Name == "AnnounceAsync" && m.GetParameters().Length == 1
                    && m.GetParameters()[0].ParameterType == typeof(CancellationToken));

            if (method is null)
            {
                var names = string.Join(",", trackerManager.GetType().GetMethods().Select(m => m.Name).Distinct());
                return $"no AnnounceAsync(CancellationToken); available: {names}";
            }

            if (method.Invoke(trackerManager, [CancellationToken.None]) is Task task)
            {
                await task.ConfigureAwait(false);
                return "completed";
            }

            return "did not return a Task";
        }
        catch (Exception ex)
        {
            return $"threw {ex.GetType().Name}: {ex.InnerException?.Message ?? ex.Message}";
        }
    }

    /// <summary>
    /// Adds the healthy public trackers alongside whatever the torrent brought of its own.
    /// </summary>
    /// <remarks>
    /// This used to apply only to torrents with no trackers at all, as a proxy for "not private".
    /// That guard is now redundant and was costing us peers: by the time this runs the torrent's
    /// metadata has been read and <c>IsPrivate</c> has already refused anything private outright, so
    /// every torrent reaching here is one we have established is safe to announce publicly. A stale
    /// tracker list is the common case, not the rare one, and supplementing it is what finds peers.
    /// </remarks>
    private async Task SeedTrackersAsync(TorrentManager manager, CancellationToken cancellationToken)
    {
        var trackers = await _trackers.GetAsync(cancellationToken).ConfigureAwait(false);

        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tier in manager.TrackerManager.Tiers)
        {
            foreach (var tracker in tier.Trackers)
            {
                existing.Add(tracker.Uri.ToString());
            }
        }

        var added = 0;
        foreach (var url in trackers.Where(u => !existing.Contains(u)))
        {
            try
            {
                await manager.TrackerManager.AddTrackerAsync(new Uri(url)).ConfigureAwait(false);
                added++;
            }
            catch (Exception ex)
            {
                // One unusable tracker is not worth failing a session over; the rest still announce.
                _logger.LogDebug(ex, "Orca stream: could not add tracker {Tracker}", url);
            }
        }

        if (added > 0)
        {
            _logger.LogInformation(
                "Orca stream: added {Added} healthy public tracker(s) alongside the torrent's own {Own}",
                added,
                existing.Count);
        }
    }

    /// <summary>Builds the engine settings the current configuration asks for.</summary>
    /// <returns>The settings.</returns>
    private EngineSettings BuildEngineSettings() => BuildEngineSettings(Config, CacheDir);

    /// <summary>
    /// Turns stored configuration into MonoTorrent engine settings.
    /// </summary>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="cacheDir">Where in-flight pieces are written.</param>
    /// <returns>The settings to apply.</returns>
    /// <remarks>
    /// Pure with respect to the engine: configuration in, settings out, so the same method serves
    /// first construction and every later live update. That symmetry is the point — two builders
    /// would eventually disagree, and the one that ran less often would be the wrong one. Being pure
    /// also makes every mapping here directly testable, which matters because most of these settings
    /// are otherwise only observable by watching a swarm.
    /// </remarks>
    internal static EngineSettings BuildEngineSettings(Configuration.PluginConfiguration config, string cacheDir)
    {
        var builder = new EngineSettingsBuilder
        {
            CacheDirectory = cacheDir,
            // Partial files keep the on-disk footprint proportional to what has actually been
            // fetched, instead of preallocating the full file for every session.
            UsePartialFiles = true,
            // Ask the router (UPnP/NAT-PMP) to open the listening port. This is the one change that
            // improves connectivity without pushing on the NAT budget below: an inbound peer costs no
            // outbound attempt at all, so peers reaching us are free where peers we chase are not.
            // Behind NAT with no forwarded port a client can only ever talk to peers that accept
            // inbound, which is roughly a third of a swarm — the rest can see us but never reach us.
            //
            // Fails soft by design: if the router refuses UPnP, or has it disabled, MonoTorrent
            // carries on outbound-only exactly as before. It is worth knowing this asks the router to
            // open a port, which is a real (if ordinary) change to the network's exposure.
            AllowPortForwarding = config.StreamAllowPortForwarding,
            AllowLocalPeerDiscovery = config.StreamEnableLocalPeerDiscovery,
            AllowedEncryption = EncryptionFor(config.StreamEncryptionMode),
            MaximumConnections = Math.Max(1, config.StreamMaxConnections),
            MaximumHalfOpenConnections = Math.Max(1, config.StreamMaxHalfOpenConnections),
            ConnectionTimeout = TimeSpan.FromSeconds(Math.Max(1, config.StreamConnectionTimeoutSeconds)),
            MaximumDownloadRate = KibibytesToBytes(config.StreamMaxDownloadRateKbps),
            MaximumUploadRate = KibibytesToBytes(config.StreamMaxUploadRateKbps),

            // A fixed port is what makes a router forward possible at all. Zero keeps MonoTorrent's
            // own behaviour of picking a fresh random port on every start, which is what shipped
            // before this was configurable — and what made inbound reachability unprovable.
            ListenEndPoints = new Dictionary<string, IPEndPoint>
            {
                ["ipv4"] = new IPEndPoint(IPAddress.Any, ClampPort(config.StreamListenPort)),
            },

            // null is MonoTorrent's "off" — it swaps in a NullDhtListener. NOT port -1: the doc
            // comment's "-1 disables" describes an older int-based API, and IPEndPoint validates
            // 0-65535, so constructing one with -1 throws before the engine ever sees it.
            //
            // The port matches the peer listener's, on UDP, which is what qBittorrent, Transmission
            // and Deluge all do — and the reason every router guide says "open port N" rather than
            // naming two. Leaving DHT on a random port (MonoTorrent's default) put it outside every
            // firewall rule and port forward an operator could write: measured 2026-08-16, the peer
            // listener sat on TCP 51413 as configured while DHT answered on UDP 38061, a number that
            // changed on every restart. Falls back to random only when the peer port is random too,
            // since there is nothing to align with then.
            DhtEndPoint = config.StreamEnableDht
                ? new IPEndPoint(IPAddress.Any, ClampPort(config.StreamListenPort))
                : null,
        };

        // Both halves or neither: advertising an address without its port, or a port without its
        // address, points peers at an endpoint that cannot answer.
        if (!string.IsNullOrWhiteSpace(config.StreamReportedAddress)
            && config.StreamReportedPort > 0
            && IPAddress.TryParse(config.StreamReportedAddress.Trim(), out var reported))
        {
            builder.ReportedListenEndPoints = new Dictionary<string, IPEndPoint>
            {
                ["ipv4"] = new IPEndPoint(reported, ClampPort(config.StreamReportedPort)),
            };
        }

        return builder.ToSettings();
    }

    /// <summary>
    /// Maps the configured encryption mode onto MonoTorrent's prioritised list.
    /// </summary>
    /// <param name="mode">Configured mode: allow, require or disable.</param>
    /// <returns>The encryption types to offer, in preference order.</returns>
    /// <remarks>
    /// The same three choices qBittorrent offers, and the order matters: MonoTorrent attempts them in
    /// the sequence given, so header-encryption first means an encrypted handshake is preferred
    /// wherever the peer supports one. An unrecognised value falls back to <c>allow</c> rather than
    /// throwing — a typo in a settings field must not be able to stop every stream on the server.
    /// </remarks>
    internal static List<EncryptionType> EncryptionFor(string? mode) => mode?.Trim().ToLowerInvariant() switch
    {
        "require" => [EncryptionType.RC4Header, EncryptionType.RC4Full],
        "disable" => [EncryptionType.PlainText],
        _ => [EncryptionType.RC4Header, EncryptionType.RC4Full, EncryptionType.PlainText],
    };

    /// <summary>Clamps a configured port into the legal range, treating anything invalid as "any".</summary>
    /// <param name="port">The configured port.</param>
    /// <returns>A port MonoTorrent will accept.</returns>
    internal static int ClampPort(int port) => port is > 0 and <= 65535 ? port : 0;

    /// <summary>Converts a KiB/s rate cap to the bytes/second MonoTorrent expects. Zero stays unlimited.</summary>
    /// <param name="kibibytes">The configured cap.</param>
    /// <returns>Bytes per second, or 0 for unlimited.</returns>
    internal static int KibibytesToBytes(int kibibytes) => kibibytes <= 0 ? 0 : kibibytes * 1024;

    /// <summary>
    /// A short string that changes whenever the applied engine settings should change.
    /// </summary>
    /// <param name="settings">The settings to describe.</param>
    /// <returns>The signature.</returns>
    /// <remarks>
    /// Cheaper and more honest than subscribing to Jellyfin's configuration-changed event: the
    /// comparison is against what is genuinely applied to the running engine, so it cannot drift out
    /// of step with the stored configuration however the change arrived.
    /// </remarks>
    private static string SignatureOf(EngineSettings settings) => string.Join(
        '|',
        settings.AllowPortForwarding,
        settings.AllowLocalPeerDiscovery,
        string.Join(',', settings.AllowedEncryption),
        settings.MaximumConnections,
        settings.MaximumHalfOpenConnections,
        settings.ConnectionTimeout,
        settings.MaximumDownloadRate,
        settings.MaximumUploadRate,
        string.Join(',', settings.ListenEndPoints.Select(p => $"{p.Key}={p.Value}")),
        string.Join(',', settings.ReportedListenEndPoints.Select(p => $"{p.Key}={p.Value}")),
        settings.DhtEndPoint);

    private ClientEngine GetOrCreateEngine()
    {
        if (_engine is not null)
        {
            return _engine;
        }

        var settings = BuildEngineSettings();
        _appliedSettings = SignatureOf(settings);
        _engine = new ClientEngine(settings);
        return _engine;
    }

    /// <summary>
    /// Applies configuration changes to the already-running engine.
    /// </summary>
    /// <param name="engine">The live engine.</param>
    /// <returns>A task.</returns>
    /// <remarks>
    /// Called before each session opens, which is often enough to feel immediate and rare enough to
    /// cost nothing — the comparison is a string equality on settings that almost never change.
    /// Failure is not fatal: the engine keeps running with what it already had, and the operator sees
    /// the old values still reported in the Observatory rather than a silent half-applied state.
    /// </remarks>
    private async Task ApplySettingsAsync(ClientEngine engine)
    {
        try
        {
            var settings = BuildEngineSettings();
            var signature = SignatureOf(settings);
            if (signature == _appliedSettings)
            {
                return;
            }

            await engine.UpdateSettingsAsync(settings).ConfigureAwait(false);
            _appliedSettings = signature;
            _logger.LogInformation(
                "Orca stream: engine settings updated — listening on {Port}, {Encryption}, "
                    + "dht={Dht} lsd={Lsd} forwarding={Forwarding}, {Conns} conns / {HalfOpen} half-open",
                settings.ListenEndPoints.TryGetValue("ipv4", out var listen) ? listen.Port : 0,
                string.Join('+', settings.AllowedEncryption),
                settings.DhtEndPoint?.Port != -1,
                settings.AllowLocalPeerDiscovery,
                settings.AllowPortForwarding,
                settings.MaximumConnections,
                settings.MaximumHalfOpenConnections);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Orca stream: could not apply updated engine settings; keeping the current ones");
        }
    }

    private void SweepIdle()
    {
        try
        {
            LogPlaybackHeartbeats();
            PauseUnreadTorrents();

            var idleAfter = TimeSpan.FromMinutes(Math.Max(1, Config.StreamSessionIdleMinutes));
            var cutoff = DateTimeOffset.UtcNow - idleAfter;

            // A kept session is finishing a download nobody is reading from, which is exactly what
            // "idle" looks like from here. Skipping it is the whole reason the flag exists.
            foreach (var session in _sessions.Values.Where(s => !s.Kept && s.LastAccess < cutoff).ToList())
            {
                if (_sessions.TryRemove(session.Token, out _))
                {
                    _logger.LogInformation("Orca stream: evicting idle session {Id}", session.Id);
                    TearDownAsync(session).GetAwaiter().GetResult();
                }
            }

            TrimCache();
        }
        catch (Exception ex)
        {
            // A sweep failure must never surface as a request error or kill the timer.
            _logger.LogWarning(ex, "Orca stream: idle sweep failed");
        }
    }

    /// <summary>
    /// Decides whether a session's torrent should stop transferring.
    /// </summary>
    /// <param name="kept">Whether the viewer asked to keep this title.</param>
    /// <param name="lastAccess">When the session was last read from.</param>
    /// <param name="now">Current time.</param>
    /// <returns>True when the torrent should be paused.</returns>
    /// <remarks>
    /// A kept session is exempt: it exists precisely to finish downloading with nobody reading it,
    /// which is indistinguishable from abandonment by every other measure here.
    /// </remarks>
    internal static bool ShouldPause(bool kept, DateTimeOffset lastAccess, DateTimeOffset now)
        => !kept && now - lastAccess >= TimeSpan.FromMinutes(PauseAfterMinutes);

    /// <summary>
    /// Stops transfers on sessions nobody is reading, without tearing them down.
    /// </summary>
    /// <remarks>
    /// The bandwidth a stopped stream keeps consuming is otherwise invisible: playback ends, the
    /// screen goes away, and the torrent carries on downloading ahead of a play head that no longer
    /// exists until the idle sweep evicts it twenty minutes later. Pausing keeps the session, its
    /// peers and its pieces, so a viewer who comes back resumes instantly — <see cref="ReadAsync"/>
    /// restarts the torrent on the next read.
    /// </remarks>
    private void PauseUnreadTorrents()
    {
        var now = DateTimeOffset.UtcNow;

        foreach (var session in _sessions.Values.Where(s => ShouldPause(s.Kept, s.LastAccess, now)))
        {
            if (session.Manager is not { } manager
                || manager.State is not (TorrentState.Downloading or TorrentState.Seeding))
            {
                continue;
            }

            try
            {
                manager.PauseAsync().GetAwaiter().GetResult();
                _logger.LogInformation(
                    "Orca stream: paused {Id} — unread for {Minutes} minute(s), so it stops using bandwidth",
                    session.Id,
                    PauseAfterMinutes);
            }
            catch (Exception ex)
            {
                // A torrent that will not pause is not worth failing the sweep over; the idle
                // eviction below still bounds it.
                _logger.LogWarning(ex, "Orca stream: could not pause {Id}", session.Id);
            }
        }
    }

    /// <summary>
    /// Records what each actively-played session delivered over the last minute.
    /// </summary>
    /// <remarks>
    /// Rides the idle sweep's existing timer rather than adding one: it already walks every session on
    /// exactly the cadence a heartbeat wants. Only sessions read from during the interval are logged,
    /// so a paused or abandoned stream goes quiet instead of filling the log.
    ///
    /// The number that matters is the delivered rate against the bitrate on the "ready" line. Above
    /// it, playback sustains; below it, the player is draining its buffer and a stall is coming.
    /// </remarks>
    private void LogPlaybackHeartbeats()
    {
        var now = DateTimeOffset.UtcNow;

        foreach (var session in _sessions.Values.Where(s => s.State == StreamSessionState.Ready))
        {
            var since = session.HeartbeatAt ?? session.FirstReadAt;
            if (since is null || session.LastAccess <= since)
            {
                // Never read, or not read since the last heartbeat: nothing happened worth a line.
                session.HeartbeatAt = now;
                continue;
            }

            var delivered = Interlocked.Read(ref session.BytesDelivered) - session.HeartbeatBytes;
            var elapsed = (now - since.Value).TotalSeconds;
            var health = session.ReadHealth();

            _logger.LogInformation(
                "Orca stream: session {Id} playing — {Rate} B/s to the player over {Sec:F0}s, "
                    + "{Stalls} stall(s) worst {WorstMs}ms; swarm {SwarmRate} B/s "
                    + "conns[{Open} open of {Peers} avail, {Seeds} seed] {Progress:F1}% {State}",
                session.Id,
                elapsed > 0 ? (long)(delivered / elapsed) : 0,
                elapsed,
                Interlocked.Exchange(ref session.Stalls, 0),
                Interlocked.Exchange(ref session.WorstStallMs, 0),
                health.DownloadRateBytesPerSecond,
                health.OpenConnections,
                health.Peers,
                health.Seeds,
                health.Progress,
                health.TorrentState);

            session.HeartbeatAt = now;
            session.HeartbeatBytes = Interlocked.Read(ref session.BytesDelivered);
        }
    }

    /// <summary>
    /// Holds the piece cache under <c>StreamCacheMaxGb</c>, deleting least-recently-used files first.
    /// </summary>
    /// <remarks>
    /// This exists because eviction stopped deleting downloaded data. Keeping the pieces is what makes
    /// a pause longer than the idle window survivable — the session is rebuilt on the next request and
    /// the bytes are already there — but unbounded it would fill the disk, so the cap that was already
    /// in the config (and never enforced) is now the thing that bounds it.
    ///
    /// Runs only when no session is live at all — see the guard below for why anything finer-grained
    /// is not safe.
    /// </remarks>
    private void TrimCache()
    {
        // Only ever trim with the engine completely idle.
        //
        // Excluding "files belonging to a live session" is not something this can do reliably: a
        // session has no picked file until metadata arrives, so during Metadata state the exclusion
        // set is EMPTY, and a multi-file torrent has pieces in files the session never names anyway.
        // Deleting under a running torrent breaks it in a way that presents as a dead swarm — the
        // hardest possible failure to trace back to a cache sweep. Waiting for idle costs nothing:
        // the cache only needs to be bounded over time, not this minute.
        if (!_sessions.IsEmpty)
        {
            return;
        }

        var budget = (long)Math.Max(1, Config.StreamCacheMaxGb) * 1024 * 1024 * 1024;
        var dir = CacheDir;
        if (!Directory.Exists(dir))
        {
            return;
        }

        var candidates = new DirectoryInfo(dir)
            .EnumerateFiles("*", SearchOption.AllDirectories)
            .Select(f => new CachedFile(f.FullName, f.Length, f.LastAccessTimeUtc))
            .ToList();

        foreach (var victim in SelectForEviction(candidates, budget))
        {
            try
            {
                System.IO.File.Delete(victim.Path);
                _logger.LogInformation("Orca stream: cache over budget — dropped {File}", Path.GetFileName(victim.Path));
            }
            catch (Exception ex)
            {
                // In use by something we did not account for, or a permissions problem. Skip it and
                // keep going: the next sweep tries again, and one undeletable file must not stop the
                // rest of the trim.
                _logger.LogDebug(ex, "Orca stream: could not drop cached file {File}", victim.Path);
            }
        }
    }

    /// <summary>One cached file as the eviction decision sees it.</summary>
    /// <param name="Path">Full path on disk.</param>
    /// <param name="Length">Size in bytes.</param>
    /// <param name="LastAccessUtc">When it was last read.</param>
    internal readonly record struct CachedFile(string Path, long Length, DateTime LastAccessUtc);

    /// <summary>
    /// Chooses which cached files to drop to get back under <paramref name="budget"/>, least recently
    /// accessed first.
    /// </summary>
    /// <param name="candidates">Files eligible for eviction — never any belonging to a live session.</param>
    /// <param name="budget">Ceiling in bytes.</param>
    /// <returns>The files to delete, in the order they should go.</returns>
    /// <remarks>
    /// Separated from the deleting so the decision can be tested without a disk. It decides what gets
    /// destroyed, which makes "returns nothing when already under budget" a property worth pinning
    /// down rather than assuming.
    /// </remarks>
    internal static List<CachedFile> SelectForEviction(IEnumerable<CachedFile> candidates, long budget)
    {
        var ordered = candidates.OrderBy(f => f.LastAccessUtc).ToList();
        var total = ordered.Sum(f => f.Length);
        var victims = new List<CachedFile>();

        foreach (var file in ordered)
        {
            if (total <= budget)
            {
                break;
            }

            victims.Add(file);
            total -= file.Length;
        }

        return victims;
    }

    private async Task TearDownAsync(StreamSession session)
    {
        // Wait for any in-flight read to finish before pulling the stream out from under it.
        // Bounded, so a read wedged on an unreachable piece can't block shutdown forever.
        // Stops an in-flight metadata fetch, which otherwise has no deadline and would keep a swarm
        // alive for a session nobody is waiting on any more.
        session.Cancellation.Cancel();

        var acquired = await session.Gate.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        try
        {
            if (session.Stream is { } stream)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Orca stream: error disposing stream for {Id}", session.Id);
        }

        // Downloaded pieces survive teardown. A viewer who pauses past the idle window, or comes back
        // to the same title later, then re-opens against data that is already on disk instead of
        // starting from zero — which is what stremio's server does, and the reason a paused film there
        // resumes instantly. TrimCache is what stops this growing without bound.
        if (session.Manager is { } manager)
        {
            await TearDownManagerAsync(manager, deleteData: false).ConfigureAwait(false);
        }

        if (acquired)
        {
            session.Gate.Release();
            session.Gate.Dispose();
        }

        // When the gate was never acquired a reader still holds it; disposing the semaphore would
        // throw under them. Leaking one SemaphoreSlim on a torn-down session is the cheaper failure.
    }

    /// <summary>
    /// Stops a torrent and removes it from the engine.
    /// </summary>
    /// <param name="manager">The torrent to stop.</param>
    /// <param name="deleteData">
    /// True to wipe the downloaded pieces as well. Only right for a session that failed: there is
    /// nothing playable to keep, and leaving the partial data would occupy the cache budget ahead of
    /// content someone actually watched.
    /// </param>
    private async Task TearDownManagerAsync(TorrentManager manager, bool deleteData)
    {
        try
        {
            await manager.StopAsync().ConfigureAwait(false);
            if (_engine is not null)
            {
                // KeepAllData, NOT CacheDataOnly. "Cache data" here means the fast-resume record and
                // the .torrent copy — not the downloaded pieces — so removing it threw away the one
                // thing that lets a re-opened torrent skip verification. Measured 2026-08-16: a
                // re-opened session with 20% of a 6.25GB file already on disk spent 1m 55s in
                // state=Hashing before it could play, connecting to zero peers the whole time,
                // because the fast-resume data had been deleted by this line.
                var mode = deleteData ? RemoveMode.CacheDataAndDownloadedData : RemoveMode.KeepAllData;
                await _engine.RemoveAsync(manager, mode).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Orca stream: error stopping torrent");
        }
    }
}
