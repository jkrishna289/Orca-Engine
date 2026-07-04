using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Wholphin.Engine.Personalization;

namespace Wholphin.Engine.Controllers;

/// <summary>
/// Inspect and recompute per-user affinity vectors (dev/verification endpoints).
/// </summary>
[ApiController]
[Route("OrcaEngine/Personalization")]
[Produces("application/json")]
public class PersonalizationController : ControllerBase
{
    private readonly IPersonalizationService _personalization;
    private readonly ITasteProfileService _tasteProfiles;

    /// <summary>
    /// Initializes a new instance of the <see cref="PersonalizationController"/> class.
    /// </summary>
    /// <param name="personalization">The personalization service.</param>
    /// <param name="tasteProfiles">The taste-profile file service.</param>
    public PersonalizationController(IPersonalizationService personalization, ITasteProfileService tasteProfiles)
    {
        _personalization = personalization;
        _tasteProfiles = tasteProfiles;
    }

    /// <summary>Returns a user's stored affinity vector.</summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The affinity vector.</returns>
    [HttpGet("Profile")]
    [AllowAnonymous]
    public async Task<ActionResult<AffinityVector>> GetProfile([FromQuery] Guid userId, CancellationToken cancellationToken)
    {
        if (userId == Guid.Empty)
        {
            return BadRequest(new { error = "userId is required." });
        }

        return await _personalization.GetAsync(userId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Recomputes a user's affinity vector from their full behavior history.</summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The freshly computed affinity vector.</returns>
    [HttpPost("Recompute")]
    [AllowAnonymous]
    public async Task<ActionResult<AffinityVector>> Recompute([FromQuery] Guid userId, CancellationToken cancellationToken)
    {
        if (userId == Guid.Empty)
        {
            return BadRequest(new { error = "userId is required." });
        }

        return await _personalization.RecomputeAsync(userId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Returns a user's persisted taste-profile file (seeds + vector + top features).</summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The taste profile, or 404 when none has been built yet.</returns>
    [HttpGet("TasteProfile")]
    [AllowAnonymous]
    public async Task<ActionResult<UserTasteProfile>> GetTasteProfile([FromQuery] Guid userId, CancellationToken cancellationToken)
    {
        if (userId == Guid.Empty)
        {
            return BadRequest(new { error = "userId is required." });
        }

        var profile = await _tasteProfiles.GetAsync(userId, cancellationToken).ConfigureAwait(false);
        if (profile is null)
        {
            return NotFound(new { error = "No taste profile yet — POST RebuildTasteProfile or accrue behavior events." });
        }

        return profile;
    }

    /// <summary>Rebuilds a user's taste-profile file from their behavior history.</summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The freshly built taste profile.</returns>
    [HttpPost("RebuildTasteProfile")]
    [AllowAnonymous]
    public async Task<ActionResult<UserTasteProfile>> RebuildTasteProfile([FromQuery] Guid userId, CancellationToken cancellationToken)
    {
        if (userId == Guid.Empty)
        {
            return BadRequest(new { error = "userId is required." });
        }

        return await _tasteProfiles.RebuildAsync(userId, cancellationToken).ConfigureAwait(false);
    }
}
