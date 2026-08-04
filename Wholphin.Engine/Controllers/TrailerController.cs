using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wholphin.Engine.Data;
using Wholphin.Engine.Data.Enums;
using Wholphin.Engine.Trailer;

namespace Wholphin.Engine.Controllers;

/// <summary>
/// Streams server-cached, low-bitrate trailers and exposes the trailer state machine. The byte-stream
/// endpoint stays 404-on-miss for backward compatibility with existing clients (which retry on a
/// miss); a miss now enqueues production on the bounded priority queue instead of spawning an
/// unbounded background download. The additive <c>/{id}/status</c> endpoint lets newer clients react
/// to explicit lifecycle states instead of inferring intent from repeated 404s.
/// </summary>
[ApiController]
[Route("OrcaEngine/Trailer")]
public class TrailerController : ControllerBase
{
    private readonly ITrailerService _trailers;
    private readonly ITrailerQueue _queue;
    private readonly ITrailerStateStore _state;
    private readonly ITrailerPredictionService _prediction;
    private readonly IWholphinDbContextFactory _factory;
    private readonly Diagnostics.IEngineMetrics _metrics;

    /// <summary>
    /// Initializes a new instance of the <see cref="TrailerController"/> class.
    /// </summary>
    public TrailerController(
        ITrailerService trailers,
        ITrailerQueue queue,
        ITrailerStateStore state,
        ITrailerPredictionService prediction,
        IWholphinDbContextFactory factory,
        Diagnostics.IEngineMetrics metrics)
    {
        _trailers = trailers;
        _queue = queue;
        _state = state;
        _prediction = prediction;
        _factory = factory;
        _metrics = metrics;
    }

    /// <summary>Streams a cached trailer for a title (404 + queued production on a miss).</summary>
    /// <param name="tmdbId">The TMDB id.</param>
    /// <param name="type">"movie" or "tv" (default movie).</param>
    /// <param name="lang">Preferred trailer audio language (ISO 639-1); used when producing a miss.</param>
    /// <returns>The trailer stream, or 404.</returns>
    [HttpGet("{tmdbId:int}")]
    [AllowAnonymous]
    public IActionResult GetTrailer(int tmdbId, [FromQuery] string? type, [FromQuery] string? lang)
    {
        var mediaType = ParseType(type);
        var path = _trailers.GetCachedPath(tmdbId, mediaType);
        if (path is not null)
        {
            // Record the hit (throttled, fire-and-forget) so the cache manager's LFU/recency is accurate.
            _ = _state.RecordAccessAsync(tmdbId, mediaType);
            return PhysicalFile(path, "video/mp4", enableRangeProcessing: true);
        }

        // Not cached yet — the focused card is the highest-priority producer.
        _queue.Enqueue(tmdbId, mediaType, TrailerPriority.FocusedCard, NormalizeLang(lang));
        return NotFound();
    }

    /// <summary>Returns the explicit lifecycle state of a title's trailer (the state machine).</summary>
    /// <param name="tmdbId">The TMDB id.</param>
    /// <param name="type">"movie" or "tv" (default movie).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The current state + whether a cached file exists.</returns>
    [HttpGet("{tmdbId:int}/status")]
    [AllowAnonymous]
    public async Task<ActionResult> GetStatus(int tmdbId, [FromQuery] string? type, CancellationToken cancellationToken)
    {
        var mediaType = ParseType(type);
        var cached = _trailers.GetCachedPath(tmdbId, mediaType) is not null;

        TrailerState state;
        if (cached)
        {
            state = TrailerState.Ready;
        }
        else if (_queue.IsQueued(tmdbId, mediaType))
        {
            state = TrailerState.Queued;
        }
        else
        {
            state = await _state.GetStateAsync(tmdbId, mediaType, cancellationToken).ConfigureAwait(false);
        }

        // Smart preview start offset (Phase 14), when the metadata pipeline has computed one; null
        // lets the client fall back to its default skip.
        int? previewStartMs = null;
        try
        {
            await using var db = _factory.Create();
            previewStartMs = await db.TrailerAssets
                .Where(t => t.TmdbId == tmdbId && t.MediaType == mediaType)
                .Select(t => t.PreviewStartMs)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            // best effort — the offset is optional
        }

        // PascalCase keys to match the rest of the engine contract the client parses (Jellyfin
        // serializes property names as-written, no camelCase policy).
        return Ok(new
        {
            TmdbId = tmdbId,
            MediaType = mediaType.ToString(),
            State = state.ToString(),
            Cached = cached,
            Available = _trailers.IsAvailable,
            PreviewStartMs = previewStartMs,
        });
    }

    /// <summary>
    /// Predictively enqueues trailer production for a set of titles the client expects the user to
    /// reach soon (row neighbours, the next hero, a detail page just opened). The client sends these
    /// at a priority below the focused card, so prefetching never delays the trailer the user is
    /// actually looking at. Idempotent — the queue coalesces already-cached / in-flight titles.
    /// </summary>
    /// <param name="items">The titles to prefetch.</param>
    /// <param name="priority">Queue priority name (default LikelyNextCard); clamped away from FocusedCard.</param>
    /// <returns>How many titles were accepted.</returns>
    [HttpPost("Prefetch")]
    [AllowAnonymous]
    public ActionResult Prefetch([FromBody] PrefetchItem[]? items, [FromQuery] string? priority, [FromQuery] string? lang)
    {
        if (!_trailers.IsAvailable || items is not { Length: > 0 })
        {
            return Ok(new { enqueued = 0, available = _trailers.IsAvailable });
        }

        var prio = ParsePrefetchPriority(priority);
        var langHint = NormalizeLang(lang);
        var enqueued = 0;
        foreach (var item in items)
        {
            if (item.TmdbId > 0)
            {
                _queue.Enqueue(item.TmdbId, ParseType(item.Type), prio, langHint);
                enqueued++;
            }
        }

        return Ok(new { enqueued, available = true });
    }

    /// <summary>Enqueues trailer production for the titles most likely to be watched (predictive warming).</summary>
    /// <param name="maxItems">Maximum titles to warm (1-60, default 15).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of titles enqueued.</returns>
    [HttpPost("Prebuffer")]
    [AllowAnonymous]
    public async Task<ActionResult> Prebuffer([FromQuery] int maxItems = 15, CancellationToken cancellationToken = default)
    {
        if (!_trailers.IsAvailable)
        {
            return Ok(new { enqueued = 0, available = false });
        }

        // Single warming path: the prediction service ranks candidates (falls back to top-rated).
        var items = await _prediction.GetWarmCandidatesAsync(Math.Clamp(maxItems, 1, 60), cancellationToken).ConfigureAwait(false);
        foreach (var (tmdbId, mediaType) in items)
        {
            _queue.Enqueue(tmdbId, mediaType, TrailerPriority.BackgroundWorker);
        }

        return Ok(new { enqueued = items.Count, available = true });
    }

    /// <summary>Trailer-subsystem observability (Phase 17): binaries, queue depth, state counts, cache stats, and counters.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A diagnostics snapshot.</returns>
    [HttpGet("Diagnostics")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult> Diagnostics(CancellationToken cancellationToken)
    {
        await using var db = _factory.Create();

        var stateCounts = await db.TrailerAssets
            .GroupBy(t => t.State)
            .Select(g => new { State = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var ready = await db.TrailerAssets
            .Where(t => t.State == Data.Enums.TrailerState.Ready)
            .Select(t => new { t.FileBytes, t.Pinned, t.AccessCount })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // The columns behind this already existed on TrailerAsset — they were just never projected,
        // so "why is this trailer missing" meant reading the server log. FailedPermanent means TMDB
        // named no trailer at all; FailedTemporary means the download or transcode fell over.
        var failures = await db.TrailerAssets
            .Where(t => t.State == Data.Enums.TrailerState.FailedPermanent
                || t.State == Data.Enums.TrailerState.FailedTemporary)
            .OrderByDescending(t => t.UpdatedAt)
            .Take(100)
            .Select(t => new
            {
                t.TmdbId,
                t.MediaType,
                State = t.State.ToString(),
                t.FailureReason,
                t.FailureCount,
                t.UpdatedAt,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var trailerMetrics = _metrics.Snapshot()
            .Where(kv => kv.Key.StartsWith("trailer.", StringComparison.Ordinal))
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        return Ok(new
        {
            Available = _trailers.IsAvailable,
            QueueDepth = _queue.Depth,
            States = stateCounts.ToDictionary(s => s.State.ToString(), s => s.Count),
            Cache = new
            {
                ReadyCount = ready.Count,
                TotalBytes = ready.Sum(r => r.FileBytes ?? 0),
                PinnedCount = ready.Count(r => r.Pinned == true || (r.AccessCount ?? 0) >= 3),
            },
            Failures = failures,
            Metrics = trailerMetrics,
        });
    }

    /// <summary>Trims a client-supplied ISO 639-1 language hint; null/blank/overlong values become null.</summary>
    private static string? NormalizeLang(string? lang)
    {
        var trimmed = lang?.Trim();
        return string.IsNullOrEmpty(trimmed) || trimmed.Length > 8 ? null : trimmed;
    }

    private static MediaType ParseType(string? type) => type?.Trim().ToLowerInvariant() switch
    {
        "tv" or "series" => MediaType.Series,
        _ => MediaType.Movie,
    };

    /// <summary>
    /// Parses a prefetch priority name, clamping it to at most <see cref="TrailerPriority.HeroBillboard"/>
    /// (never <see cref="TrailerPriority.FocusedCard"/>) so a prefetch can't outrank the card the user is on.
    /// </summary>
    private static TrailerPriority ParsePrefetchPriority(string? name)
    {
        if (!Enum.TryParse<TrailerPriority>(name, ignoreCase: true, out var parsed))
        {
            return TrailerPriority.LikelyNextCard;
        }

        return parsed < TrailerPriority.HeroBillboard ? TrailerPriority.HeroBillboard : parsed;
    }

    /// <summary>A title to prefetch (bound from the request body; type defaults to movie).</summary>
    public class PrefetchItem
    {
        /// <summary>Gets or sets the TMDB id.</summary>
        public int TmdbId { get; set; }

        /// <summary>Gets or sets "movie" or "tv" (default movie).</summary>
        public string? Type { get; set; }
    }
}

