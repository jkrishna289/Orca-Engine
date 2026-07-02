using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Wholphin.Engine.Recommendation;

namespace Wholphin.Engine.Controllers;

/// <summary>
/// Exposes the catalog-derived genre relationship graph (Crime → Thriller → Mystery, …).
/// </summary>
[ApiController]
[Route("OrcaEngine/Genres")]
[Produces("application/json")]
public class GenresController : ControllerBase
{
    private readonly IGenreGraphService _genres;

    /// <summary>
    /// Initializes a new instance of the <see cref="GenresController"/> class.
    /// </summary>
    /// <param name="genres">The genre graph service.</param>
    public GenresController(IGenreGraphService genres) => _genres = genres;

    /// <summary>Returns the genres most related to a source genre.</summary>
    /// <param name="genre">The source genre (e.g., "Crime").</param>
    /// <param name="limit">Maximum related genres (1-50, default 10).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The related genres, strongest first.</returns>
    [HttpGet("Related")]
    [AllowAnonymous]
    public async Task<ActionResult<IReadOnlyList<GenreLink>>> GetRelated(
        [FromQuery] string? genre,
        [FromQuery] int limit = 10,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(genre))
        {
            return BadRequest("A genre is required.");
        }

        return Ok(await _genres.GetRelatedAsync(genre, limit, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Returns the full genre association graph.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The association graph (source genre → ranked related genres).</returns>
    [HttpGet("Graph")]
    [AllowAnonymous]
    public async Task<ActionResult<IReadOnlyDictionary<string, List<GenreLink>>>> GetGraph(CancellationToken cancellationToken)
        => Ok(await _genres.GetGraphAsync(cancellationToken).ConfigureAwait(false));
}
