using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wholphin.Engine.Catalog;
using Wholphin.Engine.Data;
using Wholphin.Engine.Sync;

namespace Wholphin.Engine.Controllers;

/// <summary>
/// Catalog inspection + manual resync (dev/verification endpoints).
/// </summary>
[ApiController]
[Route("OrcaEngine/Catalog")]
[Produces("application/json")]
public class CatalogController : ControllerBase
{
    private readonly ILibrarySyncService _sync;
    private readonly IDiscoveryImporter _discovery;
    private readonly IAvailabilityReconciler _reconciler;
    private readonly ICatalogEnricher _enricher;
    private readonly IWatchProviderEnricher _watchProviders;
    private readonly IWholphinDbContextFactory _factory;

    /// <summary>
    /// Initializes a new instance of the <see cref="CatalogController"/> class.
    /// </summary>
    public CatalogController(
        ILibrarySyncService sync,
        IDiscoveryImporter discovery,
        IAvailabilityReconciler reconciler,
        ICatalogEnricher enricher,
        IWatchProviderEnricher watchProviders,
        IWholphinDbContextFactory factory)
    {
        _sync = sync;
        _discovery = discovery;
        _reconciler = reconciler;
        _enricher = enricher;
        _watchProviders = watchProviders;
        _factory = factory;
    }

    /// <summary>Returns catalog counts (total + by media type).</summary>
    [HttpGet("Stats")]
    [AllowAnonymous]
    public async Task<ActionResult<CatalogStats>> GetStats(CancellationToken cancellationToken)
    {
        await using var db = _factory.Create();
        var types = await db.CatalogItems
            .Select(c => c.MediaType)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var stats = new CatalogStats { CatalogItems = types.Count };
        foreach (var group in types.GroupBy(t => t))
        {
            stats.ByType[group.Key.ToString()] = group.Count();
        }

        return stats;
    }

    /// <summary>Triggers a full catalog resync in the background.</summary>
    [HttpPost("Resync")]
    [AllowAnonymous]
    public ActionResult Resync()
    {
        _ = Task.Run(() => _sync.SyncAllAsync());
        return Accepted(new { status = "resync started" });
    }

    /// <summary>
    /// Imports requestable (not-yet-available) titles from Jellyseerr discovery into the catalog.
    /// No-op unless the availability-aware discovery feature is enabled and Jellyseerr is configured.
    /// </summary>
    /// <param name="pages">Discovery pages to pull per media type (1-20).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of new requestable items added.</returns>
    [HttpPost("ImportDiscover")]
    [AllowAnonymous]
    public async Task<ActionResult> ImportDiscover([FromQuery] int pages = 1, CancellationToken cancellationToken = default)
    {
        var added = await _discovery.ImportAsync(pages, cancellationToken).ConfigureAwait(false);
        return Ok(new { added });
    }

    /// <summary>
    /// Polls Jellyseerr to advance in-flight (Requested/Downloading) items through the availability
    /// state machine. No-op unless Jellyseerr is configured.
    /// </summary>
    /// <param name="maxItems">Maximum items to reconcile this pass.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of items whose availability changed.</returns>
    [HttpPost("Reconcile")]
    [AllowAnonymous]
    public async Task<ActionResult> Reconcile([FromQuery] int maxItems = 200, CancellationToken cancellationToken = default)
    {
        var updated = await _reconciler.ReconcileAsync(maxItems, cancellationToken).ConfigureAwait(false);
        return Ok(new { updated });
    }

    /// <summary>
    /// Backfills genres + poster/backdrop artwork + trailer onto requestable catalog rows that are
    /// missing them, using TMDB. No-op unless a TMDB API key is configured.
    /// </summary>
    /// <param name="maxItems">Maximum rows to enrich this pass (1-500).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of rows enriched.</returns>
    [HttpPost("EnrichTmdb")]
    [AllowAnonymous]
    public async Task<ActionResult> EnrichTmdb([FromQuery] int maxItems = 50, CancellationToken cancellationToken = default)
    {
        var enriched = await _enricher.EnrichAsync(maxItems, cancellationToken).ConfigureAwait(false);
        return Ok(new { enriched });
    }

    /// <summary>
    /// Tags catalog rows with their primary streaming-provider brand (Netflix/Prime/Disney+/…) from TMDB
    /// watch providers and caches the logos — powering the studio/provider card tag. No-op unless a TMDB
    /// API key is configured.
    /// </summary>
    /// <param name="maxItems">Maximum rows to tag this pass (1-200).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of rows tagged with a provider brand.</returns>
    [HttpPost("EnrichProviders")]
    [AllowAnonymous]
    public async Task<ActionResult> EnrichProviders([FromQuery] int maxItems = 40, CancellationToken cancellationToken = default)
    {
        var tagged = await _watchProviders.EnrichAsync(maxItems, cancellationToken).ConfigureAwait(false);
        return Ok(new { tagged });
    }

    /// <summary>Catalog statistics response.</summary>
    public class CatalogStats
    {
        /// <summary>Gets or sets the total catalog item count.</summary>
        public int CatalogItems { get; set; }

        /// <summary>Gets the per-media-type counts.</summary>
        public Dictionary<string, int> ByType { get; } = new();
    }
}
