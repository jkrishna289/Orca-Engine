using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Wholphin.Engine.Data;
using Wholphin.Engine.Diagnostics;
using Wholphin.Engine.Personalization;
using Wholphin.Engine.Trailer;

namespace Wholphin.Engine.Controllers;

/// <summary>
/// The Orca Observatory's data plane: one composed health snapshot, the structured event log, and a
/// live stream of it.
/// </summary>
/// <remarks>
/// Administrator-only, like every other admin surface. Deliberately has no class-level
/// <c>[Produces("application/json")]</c> — <see cref="Stream"/> writes <c>text/event-stream</c>, and
/// a class-wide content type is the kind of thing that quietly breaks streaming later.
/// </remarks>
[ApiController]
[Route("OrcaEngine/Observatory")]
[Authorize(Policy = "RequiresElevation")]
public class ObservatoryController : ControllerBase
{
    /// <summary>How often the stream checks for new events.</summary>
    private static readonly TimeSpan StreamPollInterval = TimeSpan.FromSeconds(1);

    /// <summary>How long the stream may stay silent before sending a keep-alive comment.</summary>
    private static readonly TimeSpan StreamHeartbeat = TimeSpan.FromSeconds(20);

    private static readonly JsonSerializerOptions StreamJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly IEngineMetrics _metrics;
    private readonly IEngineEvents _events;
    private readonly IWholphinDbContextFactory _factory;
    private readonly ITrailerQueue _trailerQueue;
    private readonly IProfileRecomputeQueue _recomputeQueue;
    private readonly Integrations.Tmdb.ITmdbClient _tmdb;
    private readonly Integrations.Arr.IArrClient _arr;
    private readonly Integrations.Jellyseerr.IJellyseerrClient _jellyseerr;

    /// <summary>
    /// Initializes a new instance of the <see cref="ObservatoryController"/> class.
    /// </summary>
    /// <param name="metrics">Operational counters.</param>
    /// <param name="events">The structured event log.</param>
    /// <param name="factory">The database context factory.</param>
    /// <param name="trailerQueue">The trailer work queue.</param>
    /// <param name="recomputeQueue">The profile recompute queue.</param>
    /// <param name="tmdb">The TMDB client.</param>
    /// <param name="arr">The Sonarr/Radarr client.</param>
    /// <param name="jellyseerr">The Jellyseerr client.</param>
    public ObservatoryController(
        IEngineMetrics metrics,
        IEngineEvents events,
        IWholphinDbContextFactory factory,
        ITrailerQueue trailerQueue,
        IProfileRecomputeQueue recomputeQueue,
        Integrations.Tmdb.ITmdbClient tmdb,
        Integrations.Arr.IArrClient arr,
        Integrations.Jellyseerr.IJellyseerrClient jellyseerr)
    {
        _metrics = metrics;
        _events = events;
        _factory = factory;
        _trailerQueue = trailerQueue;
        _recomputeQueue = recomputeQueue;
        _tmdb = tmdb;
        _arr = arr;
        _jellyseerr = jellyseerr;
    }

    /// <summary>
    /// Serves the Orca Observatory dashboard itself.
    /// </summary>
    /// <returns>The dashboard page.</returns>
    /// <remarks>
    /// <para>
    /// Anonymous by necessity — this IS the sign-in page. It contains no data and no credentials:
    /// it is an empty shell that authenticates against Jellyfin and then calls the endpoints below,
    /// every one of which requires an administrator. Serving the shell to an unauthenticated
    /// visitor gives them a login form, nothing more.
    /// </para>
    /// <para>
    /// Served from the plugin's own route rather than as a Jellyfin dashboard page, so the
    /// Observatory has a URL of its own without needing a second service or port. Also answers on
    /// <c>/orca</c> — an address an admin can actually remember and type.
    /// </para>
    /// </remarks>
    [HttpGet("App")]
    [HttpGet("/orca")]
    [AllowAnonymous]
    [Produces("text/html")]
    public IActionResult App()
    {
        var name = $"{typeof(Plugin).Namespace}.Configuration.observatory-app.html";
        var stream = typeof(Plugin).Assembly.GetManifestResourceStream(name);
        if (stream is null)
        {
            return NotFound("Observatory page is missing from the plugin assembly.");
        }

        // The page is a build artifact that only changes with the plugin version, but it is also
        // the thing an admin re-loads when something looks wrong — so let the browser revalidate
        // rather than serve a stale copy of a dashboard that was just fixed.
        Response.Headers.CacheControl = "no-cache";
        return File(stream, "text/html; charset=utf-8");
    }

    /// <summary>
    /// Returns everything the Overview and Engine Health pages need, in one round trip.
    /// </summary>
    /// <returns>The snapshot.</returns>
    /// <remarks>
    /// Cheap on purpose: in-memory reads plus one <c>stat</c> of the database file, no queries. It
    /// is safe to poll. <c>Admin/Status</c> and <c>Admin/Diagnostics</c> hit the database and are
    /// not.
    /// </remarks>
    [HttpGet("Snapshot")]
    [Produces("application/json")]
    public ActionResult<object> Snapshot()
    {
        // Every field is read through Try, so one unavailable source degrades to a default and
        // names itself in Problems instead of failing the request. A diagnostics endpoint that
        // answers 500 tells an operator nothing except that the diagnostics are broken too.
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

        var body = new
        {
            Plugin = Plugin.Instance?.Name ?? "Orca Engine",
            Version = Plugin.Instance?.Version?.ToString() ?? "unknown",
            SchemaVersion = DatabaseInitializer.SchemaVersion,
            ServerTimeUtc = DateTime.UtcNow,
            UptimeMinutes = Math.Round((DateTime.UtcNow - Plugin.StartedUtc).TotalMinutes, 1),

            // Whole-process numbers, labelled as such. .NET has no per-assembly CPU or memory
            // accounting, so a tile claiming to show "plugin RAM" would simply be wrong. The client
            // diffs ProcessorTimeMs against wall time to get a CPU rate.
            Process = new
            {
                WorkingSetBytes = Try("process.workingSet", () => Environment.WorkingSet, 0L),
                GcHeapBytes = Try("process.gcHeap", () => GC.GetTotalMemory(false), 0L),
                ProcessorTimeMs = Try("process.cpu", () => (long)ProcessorTime().TotalMilliseconds, 0L),
                Scope = "Jellyfin process (all plugins) — per-plugin attribution is not measurable.",
            },

            // The numbers the engine actually owns and can bound.
            Engine = new
            {
                DatabaseBytes = Try("engine.databaseBytes", DatabaseBytes, 0L),
                CacheEntryLimit = Caching.InMemoryCache.EntryLimit,
                TrailerQueueDepth = Try("engine.trailerQueue", () => _trailerQueue.Depth, -1),
                RecomputeQueueDepth = Try("engine.recomputeQueue", () => _recomputeQueue.Depth, -1),
            },

            Integrations = new
            {
                TmdbConfigured = Try("integrations.tmdb", () => _tmdb.IsConfigured, false),
                ArrConfigured = Try("integrations.arr", () => _arr.IsConfigured, false),
                JellyseerrConfigured = Try("integrations.jellyseerr", () => _jellyseerr.IsConfigured, false),
            },

            // Monotonic counters. The client keeps a rolling window of snapshots and diffs
            // successive polls — that is where every rate and time-series chart comes from, with no
            // server-side retention at all.
            Counters = Try<IReadOnlyDictionary<string, long>>(
                "counters", _metrics.Snapshot, new Dictionary<string, long>()),
            LastSeq = Try("events.lastSeq", () => _events.LastSeq, 0L),
            Problems = problems,
        };

        return Ok(body);
    }

    /// <summary>Returns recent structured events, newest first.</summary>
    /// <param name="limit">Maximum events to return (1-2500, default 200).</param>
    /// <param name="errorsOnly">Return only errors, which are retained far longer.</param>
    /// <returns>The events.</returns>
    [HttpGet("Events")]
    [Produces("application/json")]
    public ActionResult<IReadOnlyList<EngineEvent>> Events(
        [FromQuery] int limit = 200,
        [FromQuery] bool errorsOnly = false)
        => Ok(_events.Recent(limit, errorsOnly));

    /// <summary>
    /// Streams events as they happen (Server-Sent Events).
    /// </summary>
    /// <param name="after">Resume after this sequence number. The <c>Last-Event-ID</c> header wins if present.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the client disconnects.</returns>
    /// <remarks>
    /// SSE rather than a WebSocket because Jellyfin's socket protocol is keyed on
    /// <c>SessionMessageType</c>, a closed enum a plugin cannot extend without squatting on an
    /// existing value. SSE needs no protocol negotiation and survives Jellyfin's response
    /// compression untouched (<c>text/event-stream</c> is not in the default MIME list).
    /// </remarks>
    [HttpGet("Stream")]
    public async Task Stream([FromQuery] long after = 0, CancellationToken cancellationToken = default)
    {
        Response.Headers.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";

        // nginx buffers proxied responses by default, which would hold the whole stream until the
        // connection closed. Jellyfin's published reverse-proxy config does not disable that
        // globally, so it has to be said per-response.
        Response.Headers["X-Accel-Buffering"] = "no";

        var cursor = after;
        if (Request.Headers.TryGetValue("Last-Event-ID", out var resume)
            && long.TryParse(resume.ToString(), out var resumeSeq))
        {
            cursor = resumeSeq;
        }

        var lastWrite = DateTime.UtcNow;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var batch = _events.Since(cursor);
                foreach (var entry in batch)
                {
                    await Response.WriteAsync(
                        $"id: {entry.Seq}\nevent: engine\ndata: {JsonSerializer.Serialize(entry, StreamJson)}\n\n",
                        cancellationToken).ConfigureAwait(false);
                    cursor = entry.Seq;
                }

                if (batch.Count > 0)
                {
                    await Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
                    lastWrite = DateTime.UtcNow;
                }
                else if (DateTime.UtcNow - lastWrite > StreamHeartbeat)
                {
                    // A comment line: keeps proxies and load balancers from reaping an idle connection.
                    await Response.WriteAsync(": ping\n\n", cancellationToken).ConfigureAwait(false);
                    await Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
                    lastWrite = DateTime.UtcNow;
                }

                await Task.Delay(StreamPollInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The dashboard navigated away or the browser closed the connection. That is how every
            // one of these ends — letting it bubble would file a normal disconnect as a server error.
        }
    }

    private static TimeSpan ProcessorTime()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return process.TotalProcessorTime;
        }
        catch (Exception)
        {
            // Some containers deny /proc access. A missing tile beats a 500 on the health page.
            return TimeSpan.Zero;
        }
    }

    /// <summary>The engine database's on-disk footprint, including the WAL sidecar files.</summary>
    private long DatabaseBytes()
    {
        var path = _factory.DatabasePath;
        return new[] { path, path + "-wal", path + "-shm" }
            .Select(p => new FileInfo(p))
            .Where(f => f.Exists)
            .Sum(f => f.Length);
    }
}
