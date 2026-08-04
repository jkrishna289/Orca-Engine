using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wholphin.Engine.Analytics;
using Wholphin.Engine.Data;

namespace Wholphin.Engine.Controllers;

/// <summary>
/// Recommendation/engagement analytics (Milestone 3+). Exposes the impression funnel
/// (shown → focused → clicked → played → completed), the local Wholphin community rating (M8), and
/// popularity leaderboards (M8).
/// </summary>
[ApiController]
[Route("OrcaEngine/Analytics")]
[Produces("application/json")]
public class AnalyticsController : ControllerBase
{
    private readonly IFunnelAnalytics _funnel;
    private readonly ICommunityRatingService _communityRatings;
    private readonly IPopularityService _popularity;
    private readonly IWholphinDbContextFactory _factory;

    /// <summary>
    /// Initializes a new instance of the <see cref="AnalyticsController"/> class.
    /// </summary>
    public AnalyticsController(IFunnelAnalytics funnel, ICommunityRatingService communityRatings, IPopularityService popularity, IWholphinDbContextFactory factory)
    {
        _funnel = funnel;
        _communityRatings = communityRatings;
        _popularity = popularity;
        _factory = factory;
    }

    /// <summary>Returns the impression funnel: an overall summary plus the busiest items.</summary>
    /// <param name="limit">Maximum items to return (1-200, default 25).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The funnel report.</returns>
    [HttpGet("Funnel")]
    [AllowAnonymous]
    public async Task<ActionResult<FunnelReport>> GetFunnel(
        [FromQuery] int limit = 25,
        CancellationToken cancellationToken = default)
        => await _funnel.BuildAsync(limit, cancellationToken).ConfigureAwait(false);

    /// <summary>Returns the Wholphin (local) + TMDB ratings for one item.</summary>
    /// <param name="catalogId">The catalog id, or…</param>
    /// <param name="jellyfinId">…the Jellyfin id, or…</param>
    /// <param name="tmdbId">…the TMDB id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The rating pair, or 404 when no such item.</returns>
    [HttpGet("CommunityRating")]
    [AllowAnonymous]
    public async Task<ActionResult<CommunityRatingDto>> GetCommunityRating(
        [FromQuery] long? catalogId,
        [FromQuery] Guid? jellyfinId,
        [FromQuery] int? tmdbId,
        CancellationToken cancellationToken = default)
    {
        await using var db = _factory.Create();
        var row = await db.CatalogItems
            .Where(c => (catalogId != null && c.Id == catalogId)
                        || (jellyfinId != null && c.JellyfinItemId == jellyfinId)
                        || (tmdbId != null && c.TmdbId == tmdbId))
            .Select(c => new CommunityRatingDto
            {
                CatalogId = c.Id,
                Title = c.Title,
                TmdbRating = c.CommunityRating,
                WholphinRating = c.WholphinRating,
                WholphinVotes = c.WholphinVotes,
            })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return row is null ? NotFound() : row;
    }

    /// <summary>Recomputes all Wholphin community ratings from local behavior signals.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of items updated.</returns>
    [HttpPost("CommunityRating/Recompute")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult> RecomputeCommunityRatings(CancellationToken cancellationToken = default)
    {
        var updated = await _communityRatings.RecomputeAllAsync(cancellationToken).ConfigureAwait(false);
        return Ok(new { updated });
    }

    /// <summary>Returns popularity leaderboards (Most Watched / Completed / Requested / Rewatched / Dropped).</summary>
    /// <param name="limit">Max items per leaderboard (1-100, default 10).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The popularity report.</returns>
    [HttpGet("Popularity")]
    [AllowAnonymous]
    public async Task<ActionResult<PopularityReport>> GetPopularity(
        [FromQuery] int limit = 10,
        CancellationToken cancellationToken = default)
        => await _popularity.BuildAsync(limit, cancellationToken).ConfigureAwait(false);

    /// <summary>The Wholphin + TMDB rating pair for an item.</summary>
    public class CommunityRatingDto
    {
        /// <summary>Gets or sets the catalog id.</summary>
        public long CatalogId { get; set; }

        /// <summary>Gets or sets the title.</summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>Gets or sets the TMDB community rating (0-10).</summary>
        public float? TmdbRating { get; set; }

        /// <summary>Gets or sets the Wholphin community rating (0-10).</summary>
        public double? WholphinRating { get; set; }

        /// <summary>Gets or sets the number of local signals behind the Wholphin rating.</summary>
        public int? WholphinVotes { get; set; }
    }
}
