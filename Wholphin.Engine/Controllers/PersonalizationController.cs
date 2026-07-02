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

    /// <summary>
    /// Initializes a new instance of the <see cref="PersonalizationController"/> class.
    /// </summary>
    /// <param name="personalization">The personalization service.</param>
    public PersonalizationController(IPersonalizationService personalization) => _personalization = personalization;

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
}
