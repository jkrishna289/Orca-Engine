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

    /// <summary>Serves a cached watch-provider logo by TMDB provider id (404 when not yet cached).</summary>
    /// <param name="providerId">The TMDB provider id.</param>
    /// <returns>The logo image, or 404.</returns>
    [HttpGet("Provider/{providerId:int}")]
    [AllowAnonymous]
    public IActionResult GetProviderLogo(int providerId)
    {
        var (path, contentType) = _logos.Resolve(providerId);
        return path is null ? NotFound() : PhysicalFile(path, contentType);
    }
}
