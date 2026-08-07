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
    private readonly ILogger<StreamController> _logger;

    /// <summary>Initializes a new instance of the <see cref="StreamController"/> class.</summary>
    /// <param name="streams">The torrent streaming service.</param>
    /// <param name="cache">Shared cache, holding the source-handle to download-URL mapping.</param>
    /// <param name="logger">Logger.</param>
    public StreamController(
        ITorrentStreamService streams,
        Wholphin.Engine.Caching.ICache cache,
        ILogger<StreamController> logger)
    {
        _streams = streams;
        _cache = cache;
        _logger = logger;
    }

    private static bool Enabled => Plugin.Instance?.Configuration?.FeatureSourceStreaming ?? false;

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

        var session = await _streams
            .StartAsync(source, request!.Season, request.Episode, cancellationToken)
            .ConfigureAwait(false);

        if (session is null)
        {
            // Either the magnet was unusable, held no video, or the concurrency cap was hit — all
            // of which the client surfaces the same way: this source didn't work, pick another.
            return StatusCode(503, "Unable to open a stream for this source.");
        }

        return Ok(new StreamSessionResult
        {
            Token = session.Token,
            Path = $"/OrcaEngine/Stream/{session.Token}/file",
            FileName = session.FileName,
            Length = session.Length,
            MediaInfo = session.MediaInfo,
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
        try
        {
            var remaining = length;
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
