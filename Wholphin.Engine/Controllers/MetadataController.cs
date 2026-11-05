using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wholphin.Engine.Data;
using Wholphin.Engine.Data.Enums;
using Wholphin.Engine.Metadata;
using Wholphin.Engine.Personalization;

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
    private readonly IMetadataAggregator _aggregator;
    private readonly Diagnostics.IEngineMetrics _metrics;
    private readonly IWholphinDbContextFactory _factory;
    private readonly IProviderManager _providers;
    private readonly Integrations.Tmdb.ITmdbClient _tmdb;
    private readonly Caching.ICache _cache;
    private readonly System.Net.Http.IHttpClientFactory _http;

    /// <summary>
    /// Initializes a new instance of the <see cref="MetadataController"/> class.
    /// </summary>
    public MetadataController(
        ITriviaProvider trivia,
        IContentWarningProvider warnings,
        IContentWarningEnricher warningEnricher,
        IMetadataAggregator aggregator,
        Diagnostics.IEngineMetrics metrics,
        IWholphinDbContextFactory factory,
        IProviderManager providers,
        Integrations.Tmdb.ITmdbClient tmdb,
        Caching.ICache cache,
        System.Net.Http.IHttpClientFactory http)
    {
        _tmdb = tmdb;
        _cache = cache;
        _http = http;
        _providers = providers;
        _trivia = trivia;
        _warnings = warnings;
        _warningEnricher = warningEnricher;
        _aggregator = aggregator;
        _metrics = metrics;
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
    /// Returns what the catalog already knows about a title — year, runtime, certificate, score,
    /// genres, the full overview, cast and crew, logo. It is the details page's source for a title that
    /// is not in the library, which otherwise has only its card. Read straight from the catalog: no
    /// provider calls, so it answers as fast as the database does.
    /// </summary>
    /// <param name="tmdbId">The TMDB id.</param>
    /// <param name="type"><c>movie</c> or <c>tv</c>; TMDB reuses ids across the two.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The details, or 404 when the catalog has no such title.</returns>
    [HttpGet("Details")]
    [AllowAnonymous]
    public async Task<ActionResult<TitleDetailsDto>> GetDetails(
        [FromQuery] int tmdbId,
        [FromQuery] string? type,
        CancellationToken cancellationToken = default)
    {
        var mediaType = type?.ToLowerInvariant() switch
        {
            "movie" => (MediaType?)MediaType.Movie,
            "tv" or "series" => MediaType.Series,
            _ => null,
        };

        await using var db = _factory.Create();
        var item = await db.CatalogItems
            .FirstOrDefaultAsync(c => c.TmdbId == tmdbId && (mediaType == null || c.MediaType == mediaType), cancellationToken)
            .ConfigureAwait(false);
        if (item is null)
        {
            return NotFound();
        }

        // No logo yet (Fanart.tv is the enricher's only source): ask Jellyfin's own image providers,
        // which carry their own TMDB key, and keep the answer so the next visit is a database read.
        if (string.IsNullOrWhiteSpace(item.LogoImageUrl)
            && await JellyfinLogoAsync(tmdbId, item.MediaType, item.Title, cancellationToken).ConfigureAwait(false) is { } logo)
        {
            item.LogoImageUrl = logo;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        // People are stored role-prefixed ("Actor:X", "Director:Y", "Writer:Z").
        var people = CatalogFeatures.Parse(item.PeopleJson);
        IReadOnlyList<string> Role(string role) => people
            .Where(p => p.StartsWith(role + ":", StringComparison.OrdinalIgnoreCase))
            .Select(p => p[(role.Length + 1)..].Trim())
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new TitleDetailsDto
        {
            CatalogId = item.Id,
            Title = item.Title,
            Year = item.ProductionYear,
            RuntimeMinutes = item.RuntimeMinutes is > 0 ? item.RuntimeMinutes : null,
            OfficialRating = string.IsNullOrWhiteSpace(item.OfficialRating) ? null : item.OfficialRating,
            CommunityRating = item.CommunityRating,
            Genres = CatalogFeatures.Parse(item.GenresJson),
            Overview = string.IsNullOrWhiteSpace(item.Overview) ? null : item.Overview,
            Cast = Role("Actor"),
            Directors = Role("Director"),
            Writers = Role("Writer"),
            Studios = CatalogFeatures.Parse(item.StudiosJson),
            CollectionName = item.CollectionName,
            LogoImageUrl = string.IsNullOrWhiteSpace(item.LogoImageUrl) ? null : item.LogoImageUrl,
        };
    }

    /// <summary>A series' seasons from TMDB, so a show that isn't (fully) in the library can still be browsed.</summary>
    /// <param name="tmdbId">The series' TMDB id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The seasons, Specials included; empty when TMDB is unset or down.</returns>
    [HttpGet("Seasons")]
    [AllowAnonymous]
    public async Task<ActionResult<IReadOnlyList<Integrations.Tmdb.SeasonInfo>>> GetSeasons(
        [FromQuery] int tmdbId,
        CancellationToken cancellationToken = default)
        => Ok(await _tmdb.GetSeasonsAsync(tmdbId, cancellationToken).ConfigureAwait(false));

    /// <summary>One season's episodes from TMDB, in order.</summary>
    /// <param name="tmdbId">The series' TMDB id.</param>
    /// <param name="season">The season number (0 is Specials).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The episodes; empty when TMDB is unset or down.</returns>
    [HttpGet("Episodes")]
    [AllowAnonymous]
    public async Task<ActionResult<IReadOnlyList<Integrations.Tmdb.EpisodeInfo>>> GetEpisodes(
        [FromQuery] int tmdbId,
        [FromQuery] int season,
        CancellationToken cancellationToken = default)
        => Ok(await _tmdb.GetSeasonEpisodesAsync(tmdbId, season, cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// Intro, recap and credits times for a streamed movie or episode, from IntroDB. A stream isn't a
    /// library item, so Jellyfin's own segments never cover it.
    /// </summary>
    /// <param name="tmdbId">The movie's or series' TMDB id.</param>
    /// <param name="type"><c>movie</c> or <c>tv</c>.</param>
    /// <param name="season">The season, for an episode.</param>
    /// <param name="episode">The episode, for an episode.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Whatever is known; every part null when nothing is.</returns>
    [HttpGet("Segments")]
    [AllowAnonymous]
    public async Task<ActionResult<SkipSegments>> GetSegments(
        [FromQuery] int tmdbId,
        [FromQuery] string? type,
        [FromQuery] int? season,
        [FromQuery] int? episode,
        CancellationToken cancellationToken = default)
    {
        var tv = type?.ToLowerInvariant() is "tv" or "series";
        if (tmdbId <= 0 || (tv && (season is null || episode is null)))
        {
            return BadRequest("tmdbId, and season and episode for tv, are required.");
        }

        var key = $"introdb:{(tv ? "tv" : "movie")}:{tmdbId}:{season}:{episode}";
        if (_cache.TryGet<SkipSegments>(key, out var known) && known is not null)
        {
            return Ok(known);
        }

        var mediaType = tv ? MediaType.Series : MediaType.Movie;
        await using var db = _factory.Create();
        var imdb = await db.CatalogItems
            .Where(c => c.TmdbId == tmdbId && c.MediaType == mediaType && c.ImdbId != null)
            .Select(c => c.ImdbId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? await _tmdb.GetImdbIdAsync(tmdbId, mediaType, cancellationToken).ConfigureAwait(false);

        SkipSegments? found = null;
        if (!string.IsNullOrWhiteSpace(imdb))
        {
            try
            {
                using var client = _http.CreateClient(Wholphin.Engine.Diagnostics.OrcaMetricsHandler.ClientName);
                using var response = await client.GetAsync(IntroDb.Url(imdb, tv ? season : null, tv ? episode : null), cancellationToken).ConfigureAwait(false);
                found = response.StatusCode == System.Net.HttpStatusCode.NotFound ? SkipSegments.None
                    : response.IsSuccessStatusCode ? IntroDb.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false))
                    : null;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                _metrics.Increment("introdb.error");   // IntroDB down or unreachable: no skip button, retried soon
            }
        }

        // Times rarely change once submitted; an untimed episode is often timed later; a failure is
        // retried soon. Every play of an episode asks, so even a miss is worth remembering a while.
        var result = found ?? SkipSegments.None;
        _cache.Set(key, result, found is null ? TimeSpan.FromMinutes(30) : found.IsEmpty ? TimeSpan.FromHours(12) : TimeSpan.FromDays(7));
        return Ok(result);
    }

    // A throwaway, never-saved Movie/Series carrying only the TMDB id is enough for the providers. No
    // provider name: every enabled image provider answers (Jellyfin's TMDB, plus Fanart.tv if installed).
    // ponytail: a miss is not remembered, so a logo-less title asks again on each visit; store a sentinel if that shows up.
    private async Task<string?> JellyfinLogoAsync(int tmdbId, MediaType type, string? title, CancellationToken ct)
    {
        try
        {
            BaseItem probe = type == MediaType.Series ? new Series() : new Movie();
            probe.Name = title;
            probe.SetProviderId(MediaBrowser.Model.Entities.MetadataProvider.Tmdb, tmdbId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var images = (await _providers.GetAvailableRemoteImages(
                probe,
                new RemoteImageQuery(string.Empty) { ImageType = ImageType.Logo, IncludeAllLanguages = true },
                ct).ConfigureAwait(false)).ToList();
            return PickLogo(images)?.Url;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;                        // providers down or unhappy with the probe: the page shows the title text
        }
    }

    /// <summary>English first, then a language-neutral logo, then whatever there is.</summary>
    internal static RemoteImageInfo? PickLogo(IReadOnlyList<RemoteImageInfo> images) =>
        images.FirstOrDefault(i => string.Equals(i.Language, "en", StringComparison.OrdinalIgnoreCase))
        ?? images.FirstOrDefault(i => string.IsNullOrEmpty(i.Language))
        ?? images.FirstOrDefault();

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
    [Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult> EnrichWarnings([FromQuery] int maxItems = 25, CancellationToken cancellationToken = default)
    {
        var processed = await _warningEnricher.EnrichAsync(maxItems, cancellationToken).ConfigureAwait(false);
        return Ok(new { processed });
    }

    /// <summary>
    /// Metadata-provider observability: per-provider health, the effective field priority, and how
    /// much of the catalog each field is actually covered for.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A diagnostics snapshot.</returns>
    /// <remarks>
    /// Reports only whether a provider is <c>Configured</c>, never the key — not even masked. Provider
    /// URLs carry their key in the query string, so nothing derived from a request is echoed either.
    /// </remarks>
    [HttpGet("Diagnostics")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult> Diagnostics(CancellationToken cancellationToken)
    {
        var priority = MetadataPriority.FromConfig(Plugin.Instance?.Configuration);

        await using var db = _factory.Create();
        var coverage = await db.CatalogItems
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Total = g.Count(),
                NeverSynced = g.Count(c => c.MetadataSyncedAt == null),
                WithPoster = g.Count(c => c.PosterImageUrl != null),
                WithLogo = g.Count(c => c.LogoImageUrl != null),
                WithRatings = g.Count(c => c.RatingsJson != null),
            })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        var counters = _metrics.Snapshot();

        return Ok(new
        {
            Providers = _aggregator.Health(),
            Priority = new
            {
                Core = priority.Order(MetadataCapability.Core),
                Artwork = priority.Order(MetadataCapability.Artwork),
                Logo = priority.Order(MetadataCapability.Logo),
                Ratings = priority.Order(MetadataCapability.Ratings),
                Episodes = priority.Order(MetadataCapability.Episodes),
            },
            Coverage = coverage,
            Metrics = counters
                .Where(kv => kv.Key.StartsWith("provider.", StringComparison.Ordinal)
                             || kv.Key.StartsWith("metadata.", StringComparison.Ordinal)
                             || kv.Key.StartsWith("cache.meta.", StringComparison.Ordinal))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
        });
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

    /// <summary>Title details response (<see cref="GetDetails"/>). Absent values are null or empty, never guessed.</summary>
    public class TitleDetailsDto
    {
        /// <summary>Gets or sets the catalog id.</summary>
        public long CatalogId { get; set; }

        /// <summary>Gets or sets the title.</summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>Gets or sets the release / first-air year.</summary>
        public int? Year { get; set; }

        /// <summary>Gets or sets the runtime (per episode for series), in minutes.</summary>
        public int? RuntimeMinutes { get; set; }

        /// <summary>Gets or sets the certificate (e.g. "PG-13").</summary>
        public string? OfficialRating { get; set; }

        /// <summary>Gets or sets the community score (0-10).</summary>
        public float? CommunityRating { get; set; }

        /// <summary>Gets or sets the genres.</summary>
        public IReadOnlyList<string> Genres { get; set; } = Array.Empty<string>();

        /// <summary>Gets or sets the full overview.</summary>
        public string? Overview { get; set; }

        /// <summary>Gets or sets the billed cast, in order.</summary>
        public IReadOnlyList<string> Cast { get; set; } = Array.Empty<string>();

        /// <summary>Gets or sets the directors.</summary>
        public IReadOnlyList<string> Directors { get; set; } = Array.Empty<string>();

        /// <summary>Gets or sets the writers.</summary>
        public IReadOnlyList<string> Writers { get; set; } = Array.Empty<string>();

        /// <summary>Gets or sets the studios / networks.</summary>
        public IReadOnlyList<string> Studios { get; set; } = Array.Empty<string>();

        /// <summary>Gets or sets the collection (franchise) name.</summary>
        public string? CollectionName { get; set; }

        /// <summary>Gets or sets the logo art URL, when the catalog has one.</summary>
        public string? LogoImageUrl { get; set; }
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
