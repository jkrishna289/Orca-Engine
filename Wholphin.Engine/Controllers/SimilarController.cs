using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wholphin.Engine.Contracts;
using Wholphin.Engine.Data;
using Wholphin.Engine.Presentation;
using Wholphin.Engine.Recommendation;

namespace Wholphin.Engine.Controllers;

/// <summary>
/// Item-to-item similarity ("More Like This" / "Similar Titles"), driven by the content-based
/// <see cref="ISimilarityService"/>. Returns a capability-gated, directly-renderable row.
/// </summary>
[ApiController]
[Route("OrcaEngine")]
[Produces("application/json")]
public class SimilarController : ControllerBase
{
    private readonly ISimilarityService _similarity;
    private readonly ICardSelector _cardSelector;
    private readonly IWholphinDbContextFactory _factory;

    /// <summary>
    /// Initializes a new instance of the <see cref="SimilarController"/> class.
    /// </summary>
    /// <param name="similarity">The similarity service.</param>
    /// <param name="cardSelector">The dynamic card selector.</param>
    /// <param name="factory">The database context factory (source id resolution).</param>
    public SimilarController(ISimilarityService similarity, ICardSelector cardSelector, IWholphinDbContextFactory factory)
    {
        _similarity = similarity;
        _cardSelector = cardSelector;
        _factory = factory;
    }

    /// <summary>Returns titles similar to a source item (identified by catalog/Jellyfin/TMDB id).</summary>
    /// <param name="catalogId">The engine catalog item id.</param>
    /// <param name="jellyfinId">The Jellyfin item id.</param>
    /// <param name="tmdbId">The TMDB id.</param>
    /// <param name="supported">Comma-separated CardType names the client supports.</param>
    /// <param name="limit">Maximum results (1-100, default 20).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The similar-titles row.</returns>
    [HttpGet("Similar")]
    [AllowAnonymous]
    public async Task<ActionResult<SimilarResponse>> GetSimilar(
        [FromQuery] long? catalogId,
        [FromQuery] Guid? jellyfinId,
        [FromQuery] int? tmdbId,
        [FromQuery] string? supported,
        [FromQuery] int limit = 20,
        CancellationToken cancellationToken = default)
    {
        var sourceId = await ResolveSourceIdAsync(catalogId, jellyfinId, tmdbId, cancellationToken).ConfigureAwait(false);
        if (sourceId is null)
        {
            return BadRequest("Provide a known catalogId, jellyfinId, or tmdbId.");
        }

        var capabilities = ParseCapabilities(supported);
        var similar = await _similarity.MoreLikeThisAsync(sourceId.Value, limit, cancellationToken).ConfigureAwait(false);

        var row = new RenderRow { Id = "similar", Title = "More Like This", RowStyle = RowStyle.Standard };
        for (var i = 0; i < similar.Count; i++)
        {
            var item = similar[i].Item;
            var card = _cardSelector.Select(item, new CardSelectionContext
            {
                RowPurpose = "similar",
                RowStyle = RowStyle.Standard,
                RankInRow = i,
                HasResume = false,
                Capabilities = capabilities,
            });
            row.Items.Add(new RenderItem { Media = MediaIdMapper.ToMediaId(item), Card = card });
        }

        return new SimilarResponse { SourceCatalogId = sourceId.Value, Row = row };
    }

    private async Task<long?> ResolveSourceIdAsync(long? catalogId, Guid? jellyfinId, int? tmdbId, CancellationToken ct)
    {
        if (catalogId is { } cid && cid > 0)
        {
            return cid;
        }

        await using var db = _factory.Create();
        if (jellyfinId is { } jid && jid != Guid.Empty)
        {
            return await db.CatalogItems.AsNoTracking()
                .Where(c => c.JellyfinItemId == jid)
                .Select(c => (long?)c.Id)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);
        }

        if (tmdbId is { } tid && tid > 0)
        {
            return await db.CatalogItems.AsNoTracking()
                .Where(c => c.TmdbId == tid)
                .Select(c => (long?)c.Id)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);
        }

        return null;
    }

    private static ClientCapabilities ParseCapabilities(string? supported)
    {
        var capabilities = new ClientCapabilities();
        if (string.IsNullOrWhiteSpace(supported))
        {
            return capabilities;
        }

        foreach (var token in supported.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Enum.TryParse<CardType>(token, ignoreCase: true, out var cardType))
            {
                capabilities.SupportedCardTypes.Add(cardType);
            }
        }

        return capabilities;
    }

    /// <summary>Response for <see cref="GetSimilar"/>.</summary>
    public class SimilarResponse
    {
        /// <summary>Gets or sets the resolved source catalog item id.</summary>
        public long SourceCatalogId { get; set; }

        /// <summary>Gets or sets the "More Like This" row.</summary>
        public RenderRow Row { get; set; } = new();
    }
}
