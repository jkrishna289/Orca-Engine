using System;
using System.Buffers;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using Wholphin.Engine.Streaming;

namespace Wholphin.Engine.Controllers;

/// <summary>Payload for opening a torrent streaming session.</summary>
public class CreateStreamSession
{
    /// <summary>
    /// Gets or sets the source id from a prior /Sources search. Preferred: the engine resolves it to
    /// the real download URL internally, so the client never holds a link containing the server's
    /// indexer API key.
    /// </summary>
    public string SourceId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a magnet link to stream directly, bypassing search. Only accepts <c>magnet:</c>
    /// URIs — an arbitrary HTTP URL here would turn this endpoint into a request forwarder that any
    /// authenticated user could aim at the server's own network.
    /// </summary>
    public string Magnet { get; set; } = string.Empty;

    /// <summary>Gets or sets the target season, when the source is a season pack.</summary>
    public int? Season { get; set; }

    /// <summary>Gets or sets the target episode, when the source is a season pack.</summary>
    public int? Episode { get; set; }
}

/// <summary>What the client needs to hand the stream to a player.</summary>
public class StreamSessionResult
{
    /// <summary>Gets or sets the capability token identifying this session.</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>Gets or sets the relative stream path (append to the server base URL).</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Gets or sets the selected file's name.</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>Gets or sets the selected file's total size in bytes.</summary>
    public long Length { get; set; }

    /// <summary>
    /// Gets or sets the probed container info, or null when it couldn't be read. Null is expected and
    /// supported — the client then lets the player discover tracks on its own.
    /// </summary>
    public StreamMediaInfo? MediaInfo { get; set; }

    /// <summary>Gets or sets the session's state: Preparing, Ready or Failed.</summary>
    public string State { get; set; } = string.Empty;
}

/// <summary>What a session is doing right now, and how its swarm is actually performing.</summary>
public class StreamSessionStatusResult
{
    /// <summary>Gets or sets the session's state: Preparing, Ready or Failed.</summary>
    public string State { get; set; } = string.Empty;

    /// <summary>Gets or sets why preparation failed, when it did. Null otherwise.</summary>
    public string? FailureReason { get; set; }

    /// <summary>Gets or sets the selected file's name. Empty until the file has been chosen.</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>Gets or sets the selected file's total size in bytes. Zero until chosen.</summary>
    public long Length { get; set; }

    /// <summary>Gets or sets the probed container info once available.</summary>
    public StreamMediaInfo? MediaInfo { get; set; }

    /// <summary>Gets or sets the count of currently reachable peers.</summary>
    public int Peers { get; set; }

    /// <summary>Gets or sets how many of those hold the complete file.</summary>
    public int Seeds { get; set; }

    /// <summary>Gets or sets how many are still downloading it themselves.</summary>
    public int Leechers { get; set; }

    /// <summary>Gets or sets the measured inbound rate in bytes per second.</summary>
    public long DownloadRateBytesPerSecond { get; set; }

    /// <summary>Gets or sets total bytes pulled from the swarm this session.</summary>
    public long DownloadedBytes { get; set; }

    /// <summary>Gets or sets completion of the whole torrent, 0-100.</summary>
    public double Progress { get; set; }

    /// <summary>Gets or sets MonoTorrent's own state name.</summary>
    public string TorrentState { get; set; } = string.Empty;

    /// <summary>Gets or sets whether the file list has arrived — false means the magnet is still resolving.</summary>
    public bool HasMetadata { get; set; }

    /// <summary>
    /// Gets or sets tracker and DHT detail behind an empty swarm.
    /// </summary>
    /// <remarks>
    /// Diagnostic only — never rendered to a viewer. It is on the API rather than the log alone
    /// because reading the server's journal needs root, which makes every "why are there no peers"
    /// question a manual round-trip.
    /// </remarks>
    public string Diagnostics { get; set; } = string.Empty;
}

/// <summary>
/// Torrent source streaming. Opening a session is an authenticated action; reading the stream is
/// authorized by the unguessable token in the URL, because the bytes are consumed by a media player
/// that cannot attach a Jellyfin auth header.
/// </summary>
[ApiController]
[Route("OrcaEngine/Stream")]
public class StreamController : ControllerBase
{
    // Large enough that a read amortizes the per-request overhead, small enough that a seek mid-file
    // doesn't sit behind a huge in-flight read (reads are serialized per session).
    private const int BufferSize = 256 * 1024;

    private readonly ITorrentStreamService _streams;
    private readonly Wholphin.Engine.Caching.ICache _cache;
    private readonly Integrations.Prowlarr.IProwlarrClient _prowlarr;
    private readonly ILogger<StreamController> _logger;

    /// <summary>Initializes a new instance of the <see cref="StreamController"/> class.</summary>
    /// <param name="streams">The torrent streaming service.</param>
    /// <param name="cache">Shared cache, holding the source-handle to download-URL mapping.</param>
    /// <param name="prowlarr">Indexer client, for the dashboard's search-health block.</param>
    /// <param name="logger">Logger.</param>
    public StreamController(
        ITorrentStreamService streams,
        Wholphin.Engine.Caching.ICache cache,
        Integrations.Prowlarr.IProwlarrClient prowlarr,
        ILogger<StreamController> logger)
    {
        _streams = streams;
        _cache = cache;
        _prowlarr = prowlarr;
        _logger = logger;
    }

    private static bool Enabled => Plugin.Instance?.Configuration?.FeatureSourceStreaming ?? false;

    /// <summary>
    /// Everything the Observatory's Torrent Streaming tab needs, in one round trip.
    /// </summary>
    /// <returns>Connectivity, live sessions and cache usage.</returns>
    /// <remarks>
    /// Admin-only, and answers even when streaming is switched off — the tab has to be able to say
    /// "this is off" rather than 404 and leave the page guessing whether the plugin is simply old.
    ///
    /// Every field goes through <c>Try</c> so one unavailable source degrades to a default and names
    /// itself in <c>Problems</c>. A diagnostics endpoint that returns 500 tells an operator only that
    /// the diagnostics are broken too.
    ///
    /// Indexer and swarm-verification counters are deliberately absent: they already ride the
    /// Observatory's shared 5-second snapshot as ordinary engine counters, and duplicating them here
    /// would create a second copy free to disagree with the first.
    /// </remarks>
    [HttpGet("Dashboard")]
    [Authorize(Policy = "RequiresElevation")]
    [Produces("application/json")]
    public ActionResult<object> Dashboard()
    {
        var problems = new List<string>();

        T Try<T>(string what, Func<T> read, T fallback)
        {
            try
            {
                return read();
            }
            catch (Exception ex)
            {
                problems.Add($"{what}: {ex.GetType().Name}: {ex.Message}");
                return fallback;
            }
        }

        var config = Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();
        var cache = Try("stream.cache", () => _streams.CacheUsage(), (0L, 0, 0L));
        var connectivity = Try<StreamConnectivity?>("stream.connectivity", () => _streams.Connectivity(), null);

        // The verdict the diagnosis reasons from. Unknown when no engine exists yet, which is the
        // honest reading — not "not reachable".
        var reachability = Enum.TryParse<InboundReachability>(connectivity?.Reachability, out var parsed)
            ? parsed
            : InboundReachability.Unknown;

        var sessions = Try<List<object>>(
            "stream.sessions",
            () => _streams.Sessions().Select(s =>
            {
                var health = s.ReadHealth();
                var state = s.State.ToString();

                // Bits per second on the container, bytes per second everywhere the swarm is measured.
                var requiredBytes = s.MediaInfo?.Bitrate is int bitrate && bitrate > 0 ? bitrate / 8 : 0;

                var diagnosis = StreamDiagnostics.Diagnose(
                    state,
                    health.HasMetadata,
                    health.OpenConnections,
                    connectivity?.MaxConnections ?? 0,
                    health.Seeds,
                    health.DownloadRateBytesPerSecond,
                    requiredBytes,
                    s.TotalStalls,
                    reachability);

                return (object)new
                {
                    s.Id,
                    s.FileName,
                    State = state,
                    s.FailureReason,
                    s.Length,
                    s.Kept,
                    Bitrate = s.MediaInfo?.Bitrate,
                    RequiredBytesPerSecond = requiredBytes,

                    // The two timings a viewer actually experiences, resolved server-side so the page
                    // never has to subtract timestamps that may be missing.
                    TimeToReadyMs = s.ReadyAt is { } ready ? (long)(ready - s.StartedAt).TotalMilliseconds : (long?)null,
                    TimeToFirstFrameMs = s.FirstReadAt is { } first ? (long)(first - s.StartedAt).TotalMilliseconds : (long?)null,

                    s.LastAccess,
                    DeliveredBytes = Interlocked.Read(ref s.BytesDelivered),
                    Stalls = s.TotalStalls,
                    WorstStallMs = Interlocked.Read(ref s.WorstStallEverMs),
                    health.Peers,
                    health.OpenConnections,
                    health.Seeds,
                    health.Leechers,
                    health.DownloadRateBytesPerSecond,
                    health.UploadRateBytesPerSecond,
                    health.DownloadedBytes,
                    health.Progress,
                    health.TorrentState,
                    health.HasMetadata,
                    Diagnosis = diagnosis.Headline,
                    Recommendation = diagnosis.Recommendation,
                    Severity = diagnosis.Severity,
                };
            }).ToList(),
            new List<object>());

        return Ok(new
        {
            Enabled,
            KeepEnabled = Enabled && !string.IsNullOrWhiteSpace(config.StreamLibraryPath),

            // Null until the first stream of this process — nothing is listening before then, and
            // reporting the configured port would claim a binding that does not exist yet.
            Connectivity = connectivity,

            Sessions = sessions,
            Limits = new
            {
                config.MaxConcurrentStreamSessions,
                config.StreamSessionIdleMinutes,
                config.StreamOpenTimeoutSeconds,
                ActiveSessions = sessions.Count,
            },
            Cache = new
            {
                UsedBytes = cache.Item1,
                Files = cache.Item2,
                BudgetBytes = cache.Item3,
            },
            Indexer = new
            {
                ProwlarrConfigured = Try("stream.prowlarr", () => _prowlarr.IsConfigured, false),
                config.SourceSearchCacheHours,
            },
            Problems = problems,
        });
    }

    /// <summary>
    /// Reports the evidence chain behind the inbound-reachability verdict.
    /// </summary>
    /// <returns>Ordered checks, each with what was actually observed, and the resulting verdict.</returns>
    /// <remarks>
    /// Deliberately NOT an external probe. Asking a third-party port-checking service would hand this
    /// server's address and torrent port to someone else, and running one ourselves needs
    /// infrastructure that does not exist. So this reports only what can be observed locally, and the
    /// verdict never exceeds that evidence: the strongest claim available is "a peer connected to us",
    /// which is genuine proof, and its absence yields <c>Pending</c> or <c>Unknown</c> — never a
    /// confident "not reachable" that a quiet swarm could equally explain.
    ///
    /// The distinction this endpoint exists to preserve: a created router mapping means the router
    /// agreed, not that a packet crossed it. CGNAT, an upstream router, a firewall rule or a mapping
    /// aimed at the wrong internal host all leave the mapping looking perfect.
    /// </remarks>
    [HttpGet("Reachability")]
    [Authorize(Policy = "RequiresElevation")]
    [Produces("application/json")]
    public ActionResult<object> Reachability()
    {
        if (!Enabled)
        {
            return NotFound();
        }

        var connectivity = _streams.Connectivity();
        if (connectivity is null)
        {
            return Ok(new
            {
                Verdict = nameof(InboundReachability.Unknown),
                Checks = new[]
                {
                    Check("Peer discovery running", false, "Not up. It normally starts with the server — check the log for a warm-up failure."),
                },
                Summary = "Nothing is listening, so reachability cannot be assessed.",
            });
        }

        var mapped = connectivity.MappingsCreated.Count > 0;
        var checks = new[]
        {
            Check(
                "Listener bound",
                connectivity.ListenerBound,
                connectivity.ListenerBound
                    ? $"Accepting connections on port {connectivity.ActualListenPort}."
                    : "No listener is bound — the port may already be in use."),
            Check(
                "Port is fixed",
                connectivity.PortIsFixed,
                connectivity.PortIsFixed
                    ? "The port survives restarts, so a router forward stays valid."
                    : "A random port is chosen each restart, so it cannot be forwarded."),
            Check(
                "Router mapping created",
                mapped,
                mapped
                    ? string.Join(", ", connectivity.MappingsCreated) + " — the router agreed, which is not proof a packet crossed it."
                    : connectivity.MappingsFailed.Count > 0
                        ? "The router refused: " + string.Join(", ", connectivity.MappingsFailed)
                        : "No UPnP device answered. Forward the port by hand instead."),
            Check(
                "Inbound peer observed",
                connectivity.InboundConnections > 0,
                connectivity.InboundConnections > 0
                    ? $"{connectivity.InboundConnections} peer(s) connected to this server — proof it is reachable."
                    : "No peer has connected inbound yet. That is not proof of unreachability; the swarm may simply not have dialled us."),
            Check(
                "DHT exchanging traffic",
                connectivity.DhtBytesReceived > 0,
                DhtDetail(connectivity)),
        };

        return Ok(new
        {
            connectivity.Reachability,
            Verdict = connectivity.Reachability,
            Checks = checks,
            Summary = connectivity.Reachability switch
            {
                nameof(InboundReachability.Reachable) =>
                    "Proven reachable — peers are connecting in, which costs no outbound connection attempt.",
                nameof(InboundReachability.NotReachable) =>
                    "Nothing is listening, so no peer can reach this server regardless of the router.",
                nameof(InboundReachability.Pending) => mapped
                    ? "Listening and mapped, but nothing has arrived yet. Give a live stream a minute; if no inbound peer ever appears, the mapping is not carrying traffic."
                    : "Listening, but with no router mapping there is little chance of inbound peers. Forward the port manually.",
                _ => "Not enough evidence to say either way.",
            },
        });
    }

    /// <summary>Shapes one reachability check for the client.</summary>
    private static object Check(string name, bool passed, string detail) => new { Name = name, Passed = passed, Detail = detail };

    /// <summary>
    /// Explains DHT's traffic in terms of what it rules in or out.
    /// </summary>
    /// <param name="c">The connectivity snapshot.</param>
    /// <returns>The detail line.</returns>
    /// <remarks>
    /// The sent/received pair separates two failures that both look like "DHT finds nothing": never
    /// having started, and queries leaving with no answer ever arriving. Only the second is the
    /// long-standing behaviour on this host, and only the sent count distinguishes them.
    /// </remarks>
    private static string DhtDetail(StreamConnectivity c)
    {
        if (!c.DhtEnabled)
        {
            return "DHT is switched off.";
        }

        if (c.DhtBytesReceived > 0)
        {
            return $"{c.DhtNodes} node(s) known; {c.DhtBytesSent} bytes sent, {c.DhtBytesReceived} received.";
        }

        return c.DhtBytesSent > 0
            ? $"{c.DhtBytesSent} bytes of queries sent and nothing received — DHT traffic is leaving but no answer arrives."
            : "No DHT traffic at all yet.";
    }

    /// <summary>Opens a streaming session for a magnet link.</summary>
    /// <param name="request">The magnet and optional season/episode target.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The session descriptor, or an error status.</returns>
    [HttpPost("Sessions")]
    [Authorize]
    public async Task<ActionResult<StreamSessionResult>> CreateSession(
        [FromBody] CreateStreamSession request,
        CancellationToken cancellationToken)
    {
        // 404 rather than 403 when disabled: the feature should be invisible, not merely refused.
        if (!Enabled)
        {
            return NotFound();
        }

        string? source = null;

        if (!string.IsNullOrWhiteSpace(request?.SourceId))
        {
            // Resolved from the cache the search populated. An unknown id means the handle expired, so
            // the client is told to search again rather than left guessing.
            if (!_cache.TryGet<string>(SourcesController.SourceHandleKey(request!.SourceId), out var url)
                || string.IsNullOrWhiteSpace(url))
            {
                return StatusCode(410, "That source is no longer available. Search again.");
            }

            source = url;
        }
        else if (!string.IsNullOrWhiteSpace(request?.Magnet))
        {
            // Scheme-checked here as well as in the service: this is the only field a caller controls
            // directly, and it must not be usable to make the server fetch an arbitrary URL.
            if (!request!.Magnet.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
            {
                return BadRequest("magnet must be a magnet: URI.");
            }

            source = request.Magnet;
        }

        if (source is null)
        {
            return BadRequest("sourceId or magnet is required.");
        }

        // A SourceId came from /Sources, whose indexers are privacy-filtered fail-closed, so its
        // infohash is safe to announce to public trackers. A caller-supplied magnet has no such
        // provenance and is left untouched.
        var fromPublicIndexer = !string.IsNullOrWhiteSpace(request?.SourceId);

        var session = await _streams
            .StartAsync(source, request!.Season, request.Episode, fromPublicIndexer, cancellationToken)
            .ConfigureAwait(false);

        if (session is null)
        {
            // Either the magnet was unusable, held no video, or the concurrency cap was hit — all
            // of which the client surfaces the same way: this source didn't work, pick another.
            return StatusCode(503, "Unable to open a stream for this source.");
        }

        // Returned while still Preparing. The client polls Status until it reports Ready, which is
        // also where it gets the swarm numbers it shows during the wait.
        return Ok(new StreamSessionResult
        {
            Token = session.Token,
            Path = $"/OrcaEngine/Stream/{session.Token}/file",
            FileName = session.FileName,
            Length = session.Length,
            MediaInfo = session.MediaInfo,
            State = session.State.ToString(),
        });
    }

    /// <summary>Reports how a session is progressing, and the live health of its swarm.</summary>
    /// <param name="token">The session token.</param>
    /// <returns>The session's state and measured swarm numbers.</returns>
    /// <remarks>
    /// Polled by the client for as long as the viewer is willing to wait. Every field is measured off
    /// the live torrent handle rather than estimated, because the client prints them on screen and a
    /// fabricated number there would be worse than showing nothing at all.
    /// </remarks>
    [HttpGet("Sessions/{token}/status")]
    [Authorize]
    public ActionResult<StreamSessionStatusResult> GetSessionStatus(string token)
    {
        if (!Enabled)
        {
            return NotFound();
        }

        // Get() also refreshes LastAccess, which is what stops a session the viewer is actively
        // waiting on from being swept out from under them as "idle".
        var session = _streams.Get(token);
        if (session is null)
        {
            // Gone means the session failed and freed its slot, or was swept for idleness. Either way
            // the client should stop polling and offer the next stream.
            return NotFound();
        }

        var health = session.ReadHealth();
        return Ok(new StreamSessionStatusResult
        {
            State = session.State.ToString(),
            FailureReason = session.FailureReason,
            FileName = session.FileName,
            Length = session.Length,
            MediaInfo = session.MediaInfo,
            Peers = health.Peers,
            Seeds = health.Seeds,
            Leechers = health.Leechers,
            DownloadRateBytesPerSecond = health.DownloadRateBytesPerSecond,
            DownloadedBytes = health.DownloadedBytes,
            Progress = health.Progress,
            TorrentState = health.TorrentState,
            HasMetadata = health.HasMetadata,
            Diagnostics = health.Diagnostics,
        });
    }

    /// <summary>Closes a streaming session.</summary>
    /// <param name="token">The session token.</param>
    /// <returns>No content.</returns>
    [HttpDelete("{token}")]
    [Authorize]
    public async Task<IActionResult> StopSession(string token)
    {
        await _streams.StopAsync(token).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// Keeps a streamed title: finishes the download, copies it into the configured library folder,
    /// and asks Jellyfin to scan it in.
    /// </summary>
    /// <param name="token">The session token.</param>
    /// <returns>202 once the work is scheduled, 404 when the session is gone or keeping is off.</returns>
    /// <remarks>
    /// Answers as soon as the work is queued, never when it completes. A torrent finishing is measured
    /// in minutes and the viewer has already left the screen that asked the question — holding the
    /// request open would only give them a spinner to watch on a decision that is already made.
    /// </remarks>
    [HttpPost("Sessions/{token}/keep")]
    [Authorize]
    public async Task<IActionResult> KeepSession(string token)
    {
        if (!Enabled)
        {
            return NotFound();
        }

        var started = await _streams.KeepAsync(token).ConfigureAwait(false);
        if (!started)
        {
            // Same answer for "session expired" and "no keep folder configured": both mean there is
            // no route to a permanent copy, and the client's response to either is identical.
            return NotFound();
        }

        return Accepted();
    }

    /// <summary>
    /// Serves the session's video with Range support. This is the endpoint the player talks to: a
    /// Range request is translated into a seek, which re-points MonoTorrent's sequential picker, so
    /// seeking in the player is what drives which pieces get fetched next.
    /// </summary>
    /// <param name="token">The session token from the stream URL.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>206 for a ranged request, 200 for a full read, 404 for an unknown session.</returns>
    [HttpGet("{token}/file")]
    [AllowAnonymous]
    public async Task<IActionResult> GetFile(string token, CancellationToken cancellationToken)
    {
        if (!Enabled)
        {
            return NotFound();
        }

        var session = _streams.Get(token);
        if (session is null)
        {
            return NotFound();
        }

        // Wait for the session to open rather than refusing. A byte-range GET on a torrent that is
        // still buffering should block, exactly as it does in stremio's server — the URL is the whole
        // API, and a player must not need to know about session states.
        //
        // Refusing was worse than it looked: Media3 treats 503 (and 404, 500) as NON-retryable, so a
        // player that asked a moment too early failed the source outright with no second attempt. The
        // debug screen's legacy buttons navigate straight to playback without waiting for Ready and
        // hit exactly that. Blocking instead means the worst case is the player's own read timeout,
        // which it *does* retry.
        while (session.State == StreamSessionState.Preparing && !cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        if (session.State != StreamSessionState.Ready)
        {
            // Failed, or the client gave up. Neither is worth a body.
            return NotFound();
        }

        var total = session.Length;
        var from = 0L;
        var to = total - 1;
        var partial = false;

        var rangeHeader = Request.Headers[HeaderNames.Range].ToString();
        if (!string.IsNullOrEmpty(rangeHeader))
        {
            if (!TryParseRange(rangeHeader, total, out from, out to))
            {
                Response.Headers[HeaderNames.ContentRange] = $"bytes */{total}";
                return StatusCode(416);
            }

            partial = true;
        }

        var length = to - from + 1;

        Response.Headers[HeaderNames.AcceptRanges] = "bytes";
        Response.ContentLength = length;
        Response.ContentType = ContentTypeFor(session.FileName);

        if (partial)
        {
            Response.Headers[HeaderNames.ContentRange] = $"bytes {from}-{to}/{total}";
            Response.StatusCode = 206;
        }

        // HEAD must advertise the same headers without a body, or players probing the URL first
        // will conclude the stream is unseekable.
        if (HttpMethods.IsHead(Request.Method))
        {
            return new EmptyResult();
        }

        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        var remaining = length;
        try
        {
            var offset = from;

            while (remaining > 0 && !cancellationToken.IsCancellationRequested)
            {
                var want = (int)Math.Min(remaining, buffer.Length);
                var read = await _streams
                    .ReadAsync(session, offset, buffer.AsMemory(0, want), cancellationToken)
                    .ConfigureAwait(false);

                if (read <= 0)
                {
                    break;
                }

                await Response.Body.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                offset += read;
                remaining -= read;
            }
        }
        catch (OperationCanceledException)
        {
            // The player closed the connection — routine during seeks, not an error.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Orca stream: read failed for session {Token}", token);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        // Content-Length was already declared, so finishing short hands the player a truncated file
        // it can only interpret as a corrupt container — the failure then looks like a bad release
        // rather than a read that gave up. Resetting the connection is the truthful signal, and it's
        // one the player already knows how to retry.
        if (remaining > 0 && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Orca stream: session {Token} delivered {Short} bytes short of the declared range",
                token,
                remaining);
            HttpContext.Abort();
        }

        return new EmptyResult();
    }

    // Single-range "bytes=from-to" only. Multi-range responses are legal HTTP but no media player
    // asks for them, and supporting them would mean multipart encoding for nothing.
    private static bool TryParseRange(string header, long total, out long from, out long to)
    {
        from = 0;
        to = total - 1;

        const string Prefix = "bytes=";
        if (!header.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var spec = header[Prefix.Length..].Split(',')[0].Trim();
        var dash = spec.IndexOf('-');
        if (dash < 0)
        {
            return false;
        }

        var startText = spec[..dash];
        var endText = spec[(dash + 1)..];

        if (startText.Length == 0)
        {
            // Suffix form "bytes=-N": the final N bytes. Players use this to read a trailing index.
            if (!long.TryParse(endText, NumberStyles.None, CultureInfo.InvariantCulture, out var suffix) || suffix <= 0)
            {
                return false;
            }

            from = Math.Max(0, total - suffix);
            return true;
        }

        if (!long.TryParse(startText, NumberStyles.None, CultureInfo.InvariantCulture, out from) || from >= total)
        {
            return false;
        }

        if (endText.Length > 0
            && long.TryParse(endText, NumberStyles.None, CultureInfo.InvariantCulture, out var end))
        {
            to = Math.Min(end, total - 1);
        }

        return to >= from;
    }

    private static string ContentTypeFor(string fileName) =>
        System.IO.Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".mp4" or ".m4v" => "video/mp4",
            ".webm" => "video/webm",
            ".avi" => "video/x-msvideo",
            ".ts" => "video/mp2t",
            ".mov" => "video/quicktime",
            _ => "video/x-matroska",
        };
}
