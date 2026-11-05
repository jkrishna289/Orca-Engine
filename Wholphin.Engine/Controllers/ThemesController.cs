using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Wholphin.Engine.Themes;

namespace Wholphin.Engine.Controllers;

/// <summary>Theme songs for series, in the library or not.</summary>
[ApiController]
[Route("OrcaEngine/Themes")]
public class ThemesController : ControllerBase
{
    private readonly IThemeSongService _themes;

    /// <summary>Initializes a new instance of the <see cref="ThemesController"/> class.</summary>
    public ThemesController(IThemeSongService themes) => _themes = themes;

    /// <summary>
    /// A series' theme as MP3. 404 when it hasn't been fetched yet, which also queues it, so the show
    /// has its theme the next time its page opens. The home screen queues every series it shows.
    /// </summary>
    /// <param name="tmdbId">The series' TMDB id.</param>
    /// <returns>The MP3, or 404.</returns>
    [HttpGet("{tmdbId:int}")]
    [AllowAnonymous]
    public IActionResult Get(int tmdbId)
    {
        if (_themes.CachedPath(tmdbId) is not { } path)
        {
            _themes.Enqueue(tmdbId);
            return NotFound();
        }

        Response.Headers.CacheControl = "public, max-age=86400";
        return PhysicalFile(path, "audio/mpeg", enableRangeProcessing: true);
    }
}
