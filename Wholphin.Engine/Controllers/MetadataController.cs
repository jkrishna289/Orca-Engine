using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wholphin.Engine.Data;
using Wholphin.Engine.Metadata;

namespace Wholphin.Engine.Controllers;

/// <summary>
/// Per-title enrichment metadata (Milestone 8). Currently serves "Did You Know?" trivia (Groq-first,
/// TMDB-keyword fallback), cached.
/// </summary>
[ApiController]
[Route("OrcaEngine/Metadata")]
[Produces("application/json")]
public class MetadataController : ControllerBase
{
    private readonly ITriviaProvider _trivia;
    private readonly IContentWarningProvider _warnings;
    private readonly IContentWarningEnricher _warningEnricher;
    private readonly IWholphinDbContextFactory _factory;

    /// <summary>
    /// Initializes a new instance of the <see cref="MetadataController"/> class.
    /// </summary>
    public MetadataController(
        ITriviaProvider trivia,
        IContentWarningProvider warnings,
        IContentWarningEnricher warningEnricher,
        IWholphinDbContextFactory factory)
    {
        _trivia = trivia;
        _warnings = warnings;
        _warningEnricher = warningEnricher;
        _factory = factory;
    }

    /// <summary>Returns "Did You Know?" trivia facts for an item.</summary>
    /// <param name="catalogId">The catalog id, or…</param>
    /// <param name="jellyfinId">…the Jellyfin id, or…</param>
    /// <param name="tmdbId">…the TMDB id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The trivia facts, or 404 when no such item.</returns>
    [HttpGet("Trivia")]
    [AllowAnonymous]
    public async Task<ActionResult<TriviaDto>> GetTrivia(
        [FromQuery] long? catalogId,
        [FromQuery] Guid? jellyfinId,
        [FromQuery] int? tmdbId,
        CancellationToken cancellationToken = default)
    {
        await using var db = _factory.Create();
        var item = await db.CatalogItems
            .FirstOrDefaultAsync(
                c => (catalogId != null && c.Id == catalogId)
                     || (jellyfinId != null && c.JellyfinItemId == jellyfinId)
                     || (tmdbId != null && c.TmdbId == tmdbId),
                cancellationToken)
            .ConfigureAwait(false);
        if (item is null)
        {
            return NotFound();
        }

        var facts = await _trivia.GetTriviaAsync(item, cancellationToken).ConfigureAwait(false);
        return new TriviaDto { CatalogId = item.Id, Title = item.Title, Facts = facts };
    }

    /// <summary>
    /// Returns the content advisories for a title (movie or series). For an episode, resolve and pass the
    /// SERIES id — warnings are stored per movie/series, never per episode. LLM-generated (Groq), cached.
    /// </summary>
    /// <param name="catalogId">The catalog id, or…</param>
    /// <param name="jellyfinId">…the Jellyfin id (the series id for episodic content), or…</param>
    /// <param name="tmdbId">…the TMDB id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The advisories, or 404 when no such item.</returns>
    [HttpGet("Warnings")]
    [AllowAnonymous]
    public async Task<ActionResult<ContentWarningsDto>> GetWarnings(
        [FromQuery] long? catalogId,
        [FromQuery] Guid? jellyfinId,
        [FromQuery] int? tmdbId,
        CancellationToken cancellationToken = default)
    {
        await using var db = _factory.Create();
        var item = await db.CatalogItems
            .FirstOrDefaultAsync(
                c => (catalogId != null && c.Id == catalogId)
                     || (jellyfinId != null && c.JellyfinItemId == jellyfinId)
                     || (tmdbId != null && c.TmdbId == tmdbId),
                cancellationToken)
            .ConfigureAwait(false);
        if (item is null)
        {
            return NotFound();
        }

        var warnings = await _warnings.GetAsync(item, cancellationToken).ConfigureAwait(false);
        return new ContentWarningsDto
        {
            CatalogId = item.Id,
            Title = item.Title,
            HasWarnings = warnings.HasWarnings,
            Summary = warnings.Summary,
            Warnings = warnings.Warnings,
        };
    }

    /// <summary>
    /// Dev/verification: pre-generates content advisories for a batch of not-yet-attempted titles via Groq.
    /// No-op unless a Groq key is configured and the feature is enabled.
    /// </summary>
    /// <param name="maxItems">Maximum titles to process this pass (1-100).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of titles processed.</returns>
    [HttpPost("EnrichWarnings")]
    [AllowAnonymous]
    public async Task<ActionResult> EnrichWarnings([FromQuery] int maxItems = 25, CancellationToken cancellationToken = default)
    {
        var processed = await _warningEnricher.EnrichAsync(maxItems, cancellationToken).ConfigureAwait(false);
        return Ok(new { processed });
    }

    /// <summary>Trivia response.</summary>
    public class TriviaDto
    {
        /// <summary>Gets or sets the catalog id.</summary>
        public long CatalogId { get; set; }

        /// <summary>Gets or sets the title.</summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>Gets or sets the trivia facts.</summary>
        public IReadOnlyList<string> Facts { get; set; } = Array.Empty<string>();
    }

    /// <summary>Content-advisory response.</summary>
    public class ContentWarningsDto
    {
        /// <summary>Gets or sets the catalog id.</summary>
        public long CatalogId { get; set; }

        /// <summary>Gets or sets the title.</summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>Gets or sets a value indicating whether any advisories apply.</summary>
        public bool HasWarnings { get; set; }

        /// <summary>Gets or sets the one-line summary.</summary>
        public string Summary { get; set; } = string.Empty;

        /// <summary>Gets or sets the advisories (fixed safety-first order).</summary>
        public IReadOnlyList<ContentWarning> Warnings { get; set; } = Array.Empty<ContentWarning>();
    }
}
