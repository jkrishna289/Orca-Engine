using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Wholphin.Engine.Data.Enums;
using Wholphin.Engine.Integrations.Jellyseerr;
using Wholphin.Engine.Requests;

namespace Wholphin.Engine.Controllers;

/// <summary>
/// Engine-proxied media requests (Milestone 2). The app calls these endpoints instead of talking
/// to Jellyseerr directly, so the engine can capture request affinity and reflect availability.
/// </summary>
[ApiController]
[Route("OrcaEngine/Requests")]
[Produces("application/json")]
public class RequestsController : ControllerBase
{
    private readonly IRequestService _requests;
    private readonly IJellyseerrClient _jellyseerr;

    /// <summary>
    /// Initializes a new instance of the <see cref="RequestsController"/> class.
    /// </summary>
    /// <param name="requests">The request orchestration service.</param>
    /// <param name="jellyseerr">The Jellyseerr client (for status lookups).</param>
    public RequestsController(IRequestService requests, IJellyseerrClient jellyseerr)
    {
        _requests = requests;
        _jellyseerr = jellyseerr;
    }

    /// <summary>Submits an engine-proxied request for a title.</summary>
    /// <param name="request">The request payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The request outcome.</returns>
    [HttpPost]
    [AllowAnonymous]
    public async Task<ActionResult<RequestResult>> Submit(
        [FromBody] CreateRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryParseMediaType(request.MediaType, out var mediaType))
        {
            return BadRequest("mediaType must be Movie/movie or Series/tv.");
        }

        var result = await _requests
            .RequestAsync(request.UserId, request.TmdbId, mediaType, request.Title, cancellationToken)
            .ConfigureAwait(false);

        return result.Outcome switch
        {
            RequestOutcome.Success => Ok(result),
            RequestOutcome.InvalidInput => BadRequest(result),
            RequestOutcome.Disabled => StatusCode(403, result),
            RequestOutcome.NotConfigured => StatusCode(503, result),
            _ => StatusCode(502, result),
        };
    }

    /// <summary>Looks up the current availability of a title via Jellyseerr.</summary>
    /// <param name="tmdbId">The TMDB id.</param>
    /// <param name="type">Movie/movie or Series/tv.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The availability (null when unknown/unreachable).</returns>
    [HttpGet("Status")]
    [AllowAnonymous]
    public async Task<ActionResult<AvailabilityStatusResponse>> GetStatus(
        [FromQuery] int tmdbId,
        [FromQuery] string? type,
        CancellationToken cancellationToken)
    {
        if (tmdbId <= 0 || !TryParseMediaType(type, out var mediaType))
        {
            return BadRequest("A positive tmdbId and a Movie/Series type are required.");
        }

        var availability = await _jellyseerr.GetAvailabilityAsync(tmdbId, mediaType, cancellationToken).ConfigureAwait(false);
        return new AvailabilityStatusResponse
        {
            TmdbId = tmdbId,
            MediaType = mediaType,
            Configured = _jellyseerr.IsConfigured,
            Availability = availability,
        };
    }

    private static bool TryParseMediaType(string? value, out MediaType mediaType)
    {
        mediaType = MediaType.Unknown;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "movie":
                mediaType = MediaType.Movie;
                return true;
            case "tv":
            case "series":
            case "show":
                mediaType = MediaType.Series;
                return true;
            default:
                return false;
        }
    }

    /// <summary>Body for <see cref="Submit"/>.</summary>
    public class CreateRequest
    {
        /// <summary>Gets or sets the Jellyfin user id making the request.</summary>
        public Guid UserId { get; set; }

        /// <summary>Gets or sets the TMDB id.</summary>
        public int TmdbId { get; set; }

        /// <summary>Gets or sets the media type ("Movie"/"movie" or "Series"/"tv").</summary>
        public string? MediaType { get; set; }

        /// <summary>Gets or sets an optional display title to store on the catalog row.</summary>
        public string? Title { get; set; }
    }

    /// <summary>Response for <see cref="GetStatus"/>.</summary>
    public class AvailabilityStatusResponse
    {
        /// <summary>Gets or sets the TMDB id.</summary>
        public int TmdbId { get; set; }

        /// <summary>Gets or sets the media type.</summary>
        public MediaType MediaType { get; set; }

        /// <summary>Gets or sets a value indicating whether Jellyseerr is configured.</summary>
        public bool Configured { get; set; }

        /// <summary>Gets or sets the resolved availability (null when unknown/unreachable).</summary>
        public AvailabilityState? Availability { get; set; }
    }
}
