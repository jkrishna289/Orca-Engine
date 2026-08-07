using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;
using Wholphin.Engine.Diagnostics;
using MonoTorrent;
using MonoTorrent.Client;

namespace Wholphin.Engine.Streaming;

/// <summary>A live torrent stream the HTTP layer can read from.</summary>
public sealed class StreamSession
{
    /// <summary>Gets the content id (torrent infohash). Used to share one session between viewers.</summary>
    public required string Id { get; init; }

    /// <summary>
    /// Gets the unguessable token that appears in the stream URL. Deliberately not the infohash:
    /// the infohash is public, and the playback URL has to work in a plain player that cannot
    /// attach an auth header, so the URL itself is the capability.
    /// </summary>
    public required string Token { get; init; }

    /// <summary>Gets the selected file's name (used for the Content-Type guess).</summary>
    public required string FileName { get; init; }

    /// <summary>Gets the selected file's total length in bytes.</summary>
    public required long Length { get; init; }

    /// <summary>
    /// Gets the probed track list, or null when ffprobe was unavailable or couldn't read the partial
    /// file. Null is a supported outcome: the client then lets the player discover tracks itself.
    /// </summary>
    public StreamMediaInfo? MediaInfo { get; internal set; }

    /// <summary>Gets the time of the most recent read, used for idle eviction.</summary>
    public DateTimeOffset LastAccess { get; internal set; } = DateTimeOffset.UtcNow;

    // Not `required`: C# forbids a required member less visible than its containing type, and these
    // are internal on purpose — callers outside the streaming layer have no business touching the
    // torrent handle. Always set by TorrentStreamService.CreateSessionAsync.
    internal TorrentManager Manager { get; init; } = null!;

    internal Stream Stream { get; init; } = null!;

    // MonoTorrent allows exactly one live stream per StreamProvider ("this stream must be disposed
    // before another stream can be created"), so every read seeks the same handle and must be
    // serialized.
    // ponytail: one lock per session serializes all reads; only matters if a client opens
    // overlapping ranges — split into a stream pool if that shows up in practice.
    internal SemaphoreSlim Gate { get; } = new(1, 1);
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
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The session, or null when the torrent is unusable or holds no video.</returns>
    Task<StreamSession?> StartAsync(string source, int? season, int? episode, CancellationToken cancellationToken);

    /// <summary>Looks up a live session by its URL token.</summary>
    /// <param name="token">The capability token from the stream URL.</param>
    /// <returns>The session, or null when unknown/evicted.</returns>
    StreamSession? Get(string token);

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
    private readonly ConcurrentDictionary<string, StreamSession> _sessions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly Timer _idleSweep;

    private ClientEngine? _engine;
    private bool _disposed;

    // Wall-clock ceiling for a probe. Generous enough for a slow-but-alive source to deliver 13MB,
    // short enough that a dead one doesn't leave the viewer staring at a spinner.
    private const int ProbeBudgetSeconds = 90;

    /// <summary>Named HTTP client used to fetch .torrent files, configured not to follow redirects.</summary>
    public const string TorrentFetchClient = "orca-torrent-fetch";

    /// <summary>Initializes a new instance of the <see cref="TorrentStreamService"/> class.</summary>
    /// <param name="appPaths">Jellyfin application paths (for the default cache directory).</param>
    /// <param name="logger">Logger.</param>
    public TorrentStreamService(
        IApplicationPaths appPaths,
        ILogger<TorrentStreamService> logger,
        MediaProbe probe,
        System.Net.Http.IHttpClientFactory httpClientFactory)
    {
        _appPaths = appPaths;
        _logger = logger;
        _probe = probe;
        _httpClientFactory = httpClientFactory;
        _idleSweep = new Timer(_ => SweepIdle(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
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
        CancellationToken cancellationToken)
    {
        // Resolve to something MonoTorrent can add. A magnet carries its infohash inline; a .torrent
        // link has to be fetched and parsed before we know what it even is.
        var resolved = await ResolveAsync(source, cancellationToken).ConfigureAwait(false);
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

            created = await CreateSessionAsync(id, resolved, season, episode, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _startGate.Release();
        }

        // Probed only after the start gate is released. It pulls ~13MB through the torrent, which on a
        // slow source takes a while, and holding the gate across that would let one bad magnet block
        // every other viewer from starting anything.
        if (created is not null)
        {
            created.MediaInfo = await ProbeSafelyAsync(created, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Orca stream: session {Id} ready — {File} ({Bytes} bytes, {Tracks} tracks)",
                created.Id,
                created.FileName,
                created.Length,
                created.MediaInfo?.Streams.Count ?? 0);
        }

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
            // Touch before the read as well as after. A read legitimately blocks while the pieces
            // under the play head arrive — sometimes for minutes on a slow source — and marking the
            // session live only on completion would let the idle sweep evict it mid-read.
            session.LastAccess = DateTimeOffset.UtcNow;

            if (session.Stream.Position != offset)
            {
                // Seeking re-points MonoTorrent's sequential picker, so a viewer jumping ahead
                // starts pulling pieces from the new position instead of finishing the old run.
                session.Stream.Seek(offset, SeekOrigin.Begin);
            }

            var read = await session.Stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            session.LastAccess = DateTimeOffset.UtcNow;
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
    /// Turns a magnet URI or a .torrent HTTP link into something addable.
    ///
    /// The HTTP path matters more than it looks: Prowlarr hands out .torrent proxy links far more often
    /// than magnets, so treating a magnet as the only acceptable input made most real search results
    /// unplayable.
    /// </summary>
    private async Task<ResolvedTorrent?> ResolveAsync(string source, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return null;
        }

        if (source.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            if (!MagnetLink.TryParse(source, out var link))
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
                    && MagnetLink.TryParse(location, out var redirected))
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

    private async Task<StreamSession?> CreateSessionAsync(
        string id,
        ResolvedTorrent resolved,
        int? season,
        int? episode,
        CancellationToken cancellationToken)
    {
        var engine = GetOrCreateEngine();
        TorrentManager? manager = null;

        // Everything below waits on peers, and peers may simply never arrive: an indexer can report
        // healthy seeder counts for a swarm that is unreachable from here. Without this bound the
        // request hangs until the client gives up minutes later, which is the worst possible answer —
        // the viewer wants to be told quickly so they can pick a different source.
        var budget = TimeSpan.FromSeconds(Math.Max(15, Config.StreamOpenTimeoutSeconds));
        using var openCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        openCts.CancelAfter(budget);
        var openToken = openCts.Token;

        try
        {
            Directory.CreateDirectory(CacheDir);
            manager = resolved.Torrent is not null
                ? await engine.AddStreamingAsync(resolved.Torrent, CacheDir).ConfigureAwait(false)
                : await engine.AddStreamingAsync(resolved.Magnet!, CacheDir).ConfigureAwait(false);

            await manager.StartAsync().ConfigureAwait(false);

            // A magnet carries no file list — the metadata exchange has to complete before there is
            // anything to choose between. A .torrent already has it, so this returns immediately.
            await manager.WaitForMetadataAsync(openToken).ConfigureAwait(false);

            var entries = manager.Files
                .Select(f => new TorrentFileEntry(f.Path, f.Length))
                .ToList();

            var picked = VideoFileSelector.Select(entries, season, episode);
            if (picked is null)
            {
                _logger.LogWarning("Orca stream: no playable video in torrent {Id}", id);
                await TearDownManagerAsync(manager).ConfigureAwait(false);
                return null;
            }

            var file = manager.Files.First(f => string.Equals(f.Path, picked.Path, StringComparison.Ordinal));

            // prebuffer: true fetches the first AND last piece before returning. The last piece
            // matters more than it looks — MKV cues and MP4 moov atoms often live at the tail, and
            // without them a player can open the file but refuse to seek.
            var stream = await manager.StreamProvider!
                .CreateStreamAsync(file, true, openToken)
                .ConfigureAwait(false);

            var session = new StreamSession
            {
                Id = id,
                Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
                FileName = Path.GetFileName(picked.Path),
                Length = picked.Length,
                Manager = manager,
                Stream = stream,
            };

            _sessions[session.Token] = session;
            return session;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Budget expired rather than the caller giving up — a dead or unreachable swarm.
            _logger.LogWarning(
                "Orca stream: {Id} did not become playable within {Seconds}s (no reachable peers?)",
                id,
                budget.TotalSeconds);

            if (manager is not null)
            {
                await TearDownManagerAsync(manager).ConfigureAwait(false);
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Orca stream: failed to start session for {Id}", id);
            if (manager is not null)
            {
                await TearDownManagerAsync(manager).ConfigureAwait(false);
            }

            return null;
        }
    }

    private ClientEngine GetOrCreateEngine()
    {
        if (_engine is not null)
        {
            return _engine;
        }

        var settings = new EngineSettingsBuilder
        {
            CacheDirectory = CacheDir,
            // Partial files keep the on-disk footprint proportional to what has actually been
            // fetched, instead of preallocating the full file for every session.
            UsePartialFiles = true,
            AllowPortForwarding = false,
            MaximumConnections = 60,
        }.ToSettings();

        _engine = new ClientEngine(settings);
        return _engine;
    }

    private void SweepIdle()
    {
        try
        {
            var idleAfter = TimeSpan.FromMinutes(Math.Max(1, Config.StreamSessionIdleMinutes));
            var cutoff = DateTimeOffset.UtcNow - idleAfter;

            foreach (var session in _sessions.Values.Where(s => s.LastAccess < cutoff).ToList())
            {
                if (_sessions.TryRemove(session.Token, out _))
                {
                    _logger.LogInformation("Orca stream: evicting idle session {Id}", session.Id);
                    TearDownAsync(session).GetAwaiter().GetResult();
                }
            }
        }
        catch (Exception ex)
        {
            // A sweep failure must never surface as a request error or kill the timer.
            _logger.LogWarning(ex, "Orca stream: idle sweep failed");
        }
    }

    private async Task TearDownAsync(StreamSession session)
    {
        // Wait for any in-flight read to finish before pulling the stream out from under it.
        // Bounded, so a read wedged on an unreachable piece can't block shutdown forever.
        var acquired = await session.Gate.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        try
        {
            await session.Stream.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Orca stream: error disposing stream for {Id}", session.Id);
        }

        await TearDownManagerAsync(session.Manager).ConfigureAwait(false);

        if (acquired)
        {
            session.Gate.Release();
            session.Gate.Dispose();
        }

        // When the gate was never acquired a reader still holds it; disposing the semaphore would
        // throw under them. Leaking one SemaphoreSlim on a torn-down session is the cheaper failure.
    }

    private async Task TearDownManagerAsync(TorrentManager manager)
    {
        try
        {
            await manager.StopAsync().ConfigureAwait(false);
            if (_engine is not null)
            {
                await _engine.RemoveAsync(manager, RemoveMode.CacheDataAndDownloadedData).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Orca stream: error stopping torrent");
        }
    }
}
