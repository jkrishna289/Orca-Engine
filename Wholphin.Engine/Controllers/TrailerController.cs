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
/// Streams server-cached, low-bitrate trailers (Milestone 8, Phase 3). The app requests a trailer by
/// TMDB id when a card holds focus; a cache hit streams instantly, a miss returns 404 (and warms the
/// cache in the background) so the app falls back gracefully.
/// </summary>
[ApiController]
[Route("OrcaEngine/Trailer")]
public class TrailerController : ControllerBase
{
    private readonly ITrailerService _trailers;
    private readonly IWholphinDbContextFactory _factory;

    /// <summary>
    /// Initializes a new instance of the <see cref="TrailerController"/> class.
    /// </summary>
    public TrailerController(ITrailerService trailers, IWholphinDbContextFactory factory)
    {
        _trailers = trailers;
        _factory = factory;
    }

    /// <summary>Streams a cached trailer for a title (404 + background warm on a miss).</summary>
    /// <param name="tmdbId">The TMDB id.</param>
    /// <param name="type">"movie" or "tv" (default movie).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The trailer stream, or 404.</returns>
    [HttpGet("{tmdbId:int}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetTrailer(int tmdbId, [FromQuery] string? type, CancellationToken cancellationToken)
    {
        var mediaType = ParseType(type);
        var path = await _trailers.GetTrailerPathAsync(tmdbId, mediaType, allowDownload: false, cancellationToken).ConfigureAwait(false);
        if (path is not null)
        {
            return PhysicalFile(path, "video/mp4", enableRangeProcessing: true);
        }

        // Not cached yet — warm it in the background so a later focus hits the cache.
        if (_trailers.IsAvailable)
        {
            _ = Task.Run(() => _trailers.GetTrailerPathAsync(tmdbId, mediaType, allowDownload: true, CancellationToken.None));
        }

        return NotFound();
    }

    /// <summary>Pre-buffers trailers for the most prominent catalog titles (top-rated WatchNow + requestable).</summary>
    /// <param name="maxItems">Maximum titles to warm (1-60, default 15).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of trailers cached.</returns>
    [HttpPost("Prebuffer")]
    [AllowAnonymous]
    public async Task<ActionResult> Prebuffer([FromQuery] int maxItems = 15, CancellationToken cancellationToken = default)
    {
        if (!_trailers.IsAvailable)
        {
            return Ok(new { cached = 0, available = false });
        }

        await using var db = _factory.Create();

        // WatchNow AND requestable titles: the discover rows are the 16:9 cards that play trailers.
        var items = await db.CatalogItems
            .Where(c => c.TmdbId != null
                && (c.Availability == AvailabilityState.WatchNow || c.Availability == AvailabilityState.Request))
            .OrderByDescending(c => c.CommunityRating)
            .Take(Math.Clamp(maxItems, 1, 60))
            .Select(c => new { TmdbId = c.TmdbId!.Value, c.MediaType })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var cached = await _trailers.PrebufferAsync(
            items.Select(i => (i.TmdbId, i.MediaType)),
            cancellationToken).ConfigureAwait(false);
        return Ok(new { cached, available = true });
    }

    private static MediaType ParseType(string? type) => type?.Trim().ToLowerInvariant() switch
    {
        "tv" or "series" => MediaType.Series,
        _ => MediaType.Movie,
    };
}
