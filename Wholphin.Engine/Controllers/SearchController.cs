using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Wholphin.Engine.Catalog;
using Wholphin.Engine.Contracts;
using Wholphin.Engine.Data;
using Wholphin.Engine.Data.Enums;
using Wholphin.Engine.Integrations.Tmdb;
using Wholphin.Engine.Presentation;

namespace Wholphin.Engine.Controllers;

/// <summary>
/// Title search beyond the library. The app's own search only asks Jellyfin, so a film you don't have
/// yet could never be found; this asks TMDB and returns the titles that aren't in the library as one
/// row, each backed by a catalog row so its details page and More Like This work.
/// </summary>
[ApiController]
[Route("OrcaEngine")]
[Produces("application/json")]
public class SearchController : ControllerBase
{
    // Search runs as the viewer types; very short queries match half of TMDB and import nothing useful.
    private const int MinQueryLength = 2;
    private const int MaxResults = 20;

    private readonly ITmdbClient _tmdb;
    private readonly ICardSelector _cardSelector;
    private readonly IWholphinDbContextFactory _factory;

    /// <summary>Initializes a new instance of the <see cref="SearchController"/> class.</summary>
    /// <param name="tmdb">TMDB, which does the searching.</param>
    /// <param name="cardSelector">Picks each result's card.</param>
    /// <param name="factory">The database context factory.</param>
    public SearchController(ITmdbClient tmdb, ICardSelector cardSelector, IWholphinDbContextFactory factory)
    {
        _tmdb = tmdb;
        _cardSelector = cardSelector;
        _factory = factory;
    }

    /// <summary>Movies and series matching <paramref name="query"/> that are not in the library.</summary>
    /// <param name="query">What the viewer typed.</param>
    /// <param name="supported">Card types the client can render (comma-separated).</param>
    /// <param name="limit">The most results to return (1-20).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>One row of results, empty when nothing matched or TMDB isn't configured.</returns>
    [HttpGet("Search")]
    [AllowAnonymous]
    public async Task<ActionResult<SearchResponse>> Search(
        [FromQuery] string? query,
        [FromQuery] string? supported,
        [FromQuery] int limit = MaxResults,
        CancellationToken cancellationToken = default)
    {
        var row = new RenderRow { Id = "search", Title = "Not in your library", RowStyle = RowStyle.Standard };
        var trimmed = query?.Trim() ?? string.Empty;
        if (trimmed.Length < MinQueryLength)
        {
            return new SearchResponse { Row = row };
        }

        var take = System.Math.Clamp(limit, 1, MaxResults);
        var results = (await _tmdb.SearchTitlesAsync(trimmed, cancellationToken).ConfigureAwait(false)).Take(take).ToList();
        if (results.Count == 0)
        {
            return new SearchResponse { Row = row };
        }

        await using var db = _factory.Create();
        var rows = await TmdbTitleRows.MatchOrImportAsync(db, results, take, cancellationToken).ConfigureAwait(false);

        var capabilities = SimilarController.ParseCapabilities(supported);
        foreach (var result in results)
        {
            // Titles already in the library are in the app's own search rows.
            if (!rows.TryGetValue((result.TmdbId, result.MediaType), out var item)
                || item.JellyfinItemId is not null
                || item.Availability == AvailabilityState.Unavailable)
            {
                continue;
            }

            var card = _cardSelector.Select(item, new CardSelectionContext
            {
                RowPurpose = "search",
                RowStyle = RowStyle.Standard,
                RankInRow = row.Items.Count,
                HasResume = false,
                Capabilities = capabilities,
            });
            row.Items.Add(new RenderItem { Media = MediaIdMapper.ToMediaId(item), Card = card });
        }

        return new SearchResponse { Row = row };
    }

    /// <summary>A search's results.</summary>
    public class SearchResponse
    {
        /// <summary>Gets or sets the row of titles not in the library.</summary>
        public RenderRow Row { get; set; } = new();
    }
}
