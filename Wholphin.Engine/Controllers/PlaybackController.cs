using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using System.Text.Json.Serialization;
using Wholphin.Engine.Data.Entities;
using Wholphin.Engine.Playback;

namespace Wholphin.Engine.Controllers;

/// <summary>Authenticated resume state. Neither a supplied user id nor a shared torrent token grants access.</summary>
[ApiController]
[Authorize]
[Route("OrcaEngine/Playback")]
[Produces("application/json")]
public sealed class PlaybackController : ControllerBase
{
    private readonly IPlaybackProgressService _progress;
    private readonly IJellyfinProgressBridge _bridge;
    private readonly IAuthorizationContext _authorization;

    public PlaybackController(IPlaybackProgressService progress, IJellyfinProgressBridge bridge, IAuthorizationContext authorization)
    {
        _progress = progress;
        _bridge = bridge;
        _authorization = authorization;
    }

    [HttpPost("Sessions")]
    public async Task<ActionResult<PlaybackProgressResponse>> Start([FromBody] StartPlaybackRequest request, CancellationToken cancellationToken)
    {
        var userId = (await _authorization.GetAuthorizationInfo(HttpContext).ConfigureAwait(false)).UserId;
        if (userId == Guid.Empty) return Unauthorized();
        try
        {
            var identity = new PlaybackIdentity(request.TmdbId, request.MediaType, request.Season ?? 0, request.Episode ?? 0).Validate();
            await _bridge.ObserveExistingAsync(userId, identity, cancellationToken).ConfigureAwait(false);
            await _bridge.ReconcileUserAsync(userId, cancellationToken).ConfigureAwait(false);
            var row = await _progress.StartAsync(userId, identity, request.Title, request.Year, cancellationToken).ConfigureAwait(false);
            return Ok(PlaybackProgressResponse.From(row));
        }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpGet("Progress")]
    public async Task<ActionResult<PlaybackProgressResponse?>> Get([FromQuery] int tmdbId, [FromQuery] string mediaType,
        [FromQuery] int? season = null, [FromQuery] int? episode = null, CancellationToken cancellationToken = default)
    {
        var userId = (await _authorization.GetAuthorizationInfo(HttpContext).ConfigureAwait(false)).UserId;
        if (userId == Guid.Empty) return Unauthorized();
        try
        {
            var identity = new PlaybackIdentity(tmdbId, mediaType, season ?? 0, episode ?? 0).Validate();
            await _bridge.ObserveExistingAsync(userId, identity, cancellationToken).ConfigureAwait(false);
            await _bridge.ReconcileUserAsync(userId, cancellationToken).ConfigureAwait(false);
            var row = await _progress.GetAsync(userId, identity, cancellationToken).ConfigureAwait(false);
            // Ok(null) may be converted to 204 by MVC's no-content formatter. Keep the nullable JSON contract.
            return new JsonResult(row is null ? null : PlaybackProgressResponse.From(row)) { StatusCode = StatusCodes.Status200OK };
        }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPost("Sessions/{sessionId:guid}/Progress")]
    public async Task<IActionResult> Report(Guid sessionId, [FromBody] PlaybackProgressUpdate update, CancellationToken cancellationToken)
    {
        var userId = (await _authorization.GetAuthorizationInfo(HttpContext).ConfigureAwait(false)).UserId;
        if (userId == Guid.Empty) return Unauthorized();
        PlaybackReportResult result;
        try { result = await _progress.ReportAsync(userId, sessionId, update, cancellationToken).ConfigureAwait(false); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        if (result == PlaybackReportResult.NotFound) return NotFound();
        if (result == PlaybackReportResult.Superseded) return Conflict(new { error = "This playback session is no longer current." });
        // Heartbeats only persist progress. The bridge's background retry and home/read paths
        // perform library handoff; scanning the user's entire library here every five seconds
        // delays acknowledgments and can make a successful durable write look like a timeout.
        return Ok(new { Saved = true });
    }
}

public sealed class StartPlaybackRequest
{
    [JsonPropertyName("TmdbId")]
    public int TmdbId { get; set; }
    [JsonPropertyName("MediaType")]
    public string MediaType { get; set; } = "movie";
    [JsonPropertyName("Season")]
    public int? Season { get; set; }
    [JsonPropertyName("Episode")]
    public int? Episode { get; set; }
    [JsonPropertyName("Title")]
    public string? Title { get; set; }
    [JsonPropertyName("Year")]
    public int? Year { get; set; }
}

public sealed record PlaybackProgressResponse(
    [property: JsonPropertyName("SessionId")] Guid SessionId,
    [property: JsonPropertyName("PositionMs")] long PositionMs,
    [property: JsonPropertyName("DurationMs")] long DurationMs,
    [property: JsonPropertyName("Completed")] bool Completed,
    [property: JsonPropertyName("JellyfinItemId")] Guid? JellyfinItemId)
{
    public static PlaybackProgressResponse From(PlaybackProgressRecord row) => new(row.ActiveSessionId, row.PositionMs, row.DurationMs, row.Completed, row.JellyfinItemId);
}
