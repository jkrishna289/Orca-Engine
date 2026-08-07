using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Wholphin.Engine.Integrations.Tmdb;

namespace Wholphin.Engine.Controllers;

/// <summary>
/// Serves engine-cached images to the app. Currently: TMDB watch-provider logos for the studio/provider
/// card tag. AllowAnonymous so the app's image loader (no Jellyfin auth header) can fetch them — same as
/// the trailer stream endpoint.
/// </summary>
[ApiController]
[Route("OrcaEngine/Images")]
public class ImagesController : ControllerBase
{
    private readonly IProviderLogoCache _logos;

    /// <summary>
    /// Initializes a new instance of the <see cref="ImagesController"/> class.
    /// </summary>
    public ImagesController(IProviderLogoCache logos)
    {
        _logos = logos;
    }

    /// <summary>How long a client may keep a cached image without revalidating.</summary>
    /// <remarks>
    /// The content at a given key never changes — a provider logo is replaced by a new key, not
    /// edited in place — so this is safe to set aggressively. Without it, <c>PhysicalFileResult</c>
    /// still emits an ETag and answers 304, but every card on every render pays a round trip to be
    /// told nothing changed.
    /// </remarks>
    private const string ImmutableFor30Days = "public, max-age=2592000, immutable";

    /// <summary>Serves a cached watch-provider logo by TMDB provider id (404 when not yet cached).</summary>
    /// <param name="providerId">The TMDB provider id.</param>
    /// <returns>The logo image, or 404.</returns>
    [HttpGet("Provider/{providerId:int}")]
    [AllowAnonymous]
    public IActionResult GetProviderLogo(int providerId)
    {
        var (path, contentType) = _logos.Resolve(providerId);
        if (path is null)
        {
            return NotFound();
        }

        Response.Headers.CacheControl = ImmutableFor30Days;
        return PhysicalFile(path, contentType);
    }
}
