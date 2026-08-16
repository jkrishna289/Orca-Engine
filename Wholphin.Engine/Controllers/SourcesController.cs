using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Wholphin.Engine.Caching;
using Wholphin.Engine.Integrations.Prowlarr;
using Wholphin.Engine.Streaming;

namespace Wholphin.Engine.Controllers;

/// <summary>
/// Stream source discovery. Searching only ever happens because a viewer pressed "Find streaming
/// sources" — there is no background or speculative search anywhere, which is what keeps indexer load
/// proportional to actual intent.
/// </summary>
[ApiController]
[Route("OrcaEngine/Sources")]
[Produces("application/json")]
public class SourcesController : ControllerBase
{
    /// <summary>
    /// How long a "found nothing" answer is trusted. Long enough to absorb a viewer pressing the
    /// button twice, short enough that a transient indexer failure doesn't outlive itself.
    /// </summary>
    private static readonly TimeSpan EmptyResultTtl = TimeSpan.FromMinutes(3);

    private readonly IProwlarrClient _prowlarr;
    private readonly ICache _cache;
    private readonly Streaming.SwarmScraper _swarm;
    private readonly ILogger<SourcesController> _logger;

    /// <summary>Initializes a new instance of the <see cref="SourcesController"/> class.</summary>
    /// <param name="prowlarr">The indexer search client.</param>
    /// <param name="cache">The engine's shared in-memory cache.</param>
    /// <param name="swarm">Measures real swarm health, replacing the indexer's claims.</param>
    /// <param name="logger">Logger.</param>
    public SourcesController(
        IProwlarrClient prowlarr,
        ICache cache,
        Streaming.SwarmScraper swarm,
        ILogger<SourcesController> logger)
    {
        _prowlarr = prowlarr;
        _cache = cache;
        _swarm = swarm;
        _logger = logger;
    }

    private static Configuration.PluginConfiguration Config =>
        Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();

    /// <summary>Cache key mapping a source handle to its real download URL.</summary>
    /// <param name="id">The opaque source id handed to the client.</param>
    /// <returns>The cache key.</returns>
    internal static string SourceHandleKey(string id) => $"source-handle:{id}";

    /// <summary>
    /// What a source handle resolves to when the viewer picks it: a magnet when the indexer told us
    /// the infohash, otherwise its download link.
    /// </summary>
    /// <param name="source">The ranked source.</param>
    /// <returns>A magnet URI, or the original download URL.</returns>
    /// <remarks>
    /// Measured: a YTS source showed **14 verified seeders** in the picker and then failed to open
    /// with 503 every time, because its Prowlarr download link could not be fetched into anything
    /// usable. The infohash was right there in the search result the whole time — the swarm scrape
    /// had already used it to measure that swarm. Building the magnet from it removes the HTTP round
    /// trip that was failing, cannot 404, and takes the magnet path, which is where the healthy
    /// tracker list gets injected.
    ///
    /// The display name is carried as <c>dn</c> only as a courtesy to logs; nothing depends on it.
    /// </remarks>
    private static string HandleFor(Streaming.TorrentSource source)
    {
        var hash = Streaming.SwarmScraper.Normalise(source.InfoHash);
        if (hash is null)
        {
            return source.DownloadUrl;
        }

        return $"magnet:?xt=urn:btih:{hash}&dn={Uri.EscapeDataString(source.Title)}";
    }

    /// <summary>
    /// Finds streamable sources for a title.
    /// </summary>
    /// <param name="title">The title to search for.</param>
    /// <param name="year">Release year, which sharply improves match quality when supplied.</param>
    /// <param name="type">"movie" (default) or "tv".</param>
    /// <param name="season">Season number for an episode search.</param>
    /// <param name="episode">Episode number for an episode search.</param>
    /// <param name="preferredHeight">The display's vertical resolution, used to bias ranking.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Grouped, ranked sources.</returns>
    [HttpGet]
    [Authorize]
    public async Task<ActionResult<SourceGroups>> Search(
        [FromQuery] string? title,
        [FromQuery] int? year,
        [FromQuery] string? type,
        [FromQuery] int? season,
        [FromQuery] int? episode,
        [FromQuery] int? preferredHeight,
        CancellationToken cancellationToken)
    {
        // 404 rather than 403: with the feature off it should look absent, not forbidden.
        if (!Config.FeatureSourceStreaming)
        {
            return NotFound();
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            return BadRequest("title is required.");
        }

        if (!_prowlarr.IsConfigured)
        {
            // Distinguishable from "searched and found nothing" so the app can say something useful.
            return StatusCode(503, "No indexer is configured on the server.");
        }

        var movie = !string.Equals(type, "tv", StringComparison.OrdinalIgnoreCase);
        var query = BuildQuery(title!, year, season, episode, movie);
        var height = preferredHeight is > 0 ? preferredHeight.Value : 1080;
        var cacheKey = $"sources:{query.ToLowerInvariant()}:{height}";

        if (_cache.TryGet<SourceGroups>(cacheKey, out var cached) && cached is not null)
        {
            _logger.LogDebug("Orca Engine: source cache hit for {Query}.", query);
            return Ok(cached);
        }

        var candidates = await _prowlarr.SearchAsync(query, movie, cancellationToken).ConfigureAwait(false);

        // Measure the swarms before ranking, because the ranker both orders and *filters* on seeder
        // count. Indexer-claimed counts are routinely fiction, so ranking on them promotes torrents
        // that cannot play and prints an invented "N sharing" to the viewer. Verification is
        // fail-safe: a source whose tracker did not answer keeps its claimed count rather than being
        // zeroed out of existence.
        await _swarm.VerifyAsync(candidates, cancellationToken).ConfigureAwait(false);

        var groups = SourceRanker.Rank(candidates, height);

        // Remember id -> download URL so a later session request can resolve it without the client
        // ever having held the URL (which carries the Prowlarr API key). Cached a good deal longer
        // than the search itself: a viewer may sit on the picker, or come back to it, well after the
        // search results would have expired.
        foreach (var s in groups.All)
        {
            _cache.Set(SourceHandleKey(s.Id), HandleFor(s), TimeSpan.FromHours(24));
        }

        // An empty result gets a deliberately short TTL. "No sources" is far more often a failed
        // search than a genuine absence — an indexer rate-limiting, still warming up after a restart,
        // or briefly unreachable all look identical from here. Caching that for the full window turns
        // a blip into hours of a title being wrongly unavailable, which is exactly what happened in
        // testing: a search during server startup returned nothing and poisoned the entry for a title
        // that had three working sources minutes earlier.
        var ttl = groups.All.Count > 0
            ? TimeSpan.FromHours(Math.Max(1, Config.SourceSearchCacheHours))
            : EmptyResultTtl;

        _cache.Set(cacheKey, groups, ttl);

        _logger.LogInformation(
            "Orca Engine: source search {Query} -> {Count} usable sources.",
            query,
            groups.All.Count);

        return Ok(groups);
    }

    // Indexers match on release names, so the query mimics one: "Title Year" for a film, "Title SxxEyy"
    // for an episode. The year is what stops a remake's results burying the original's.
    private static string BuildQuery(string title, int? year, int? season, int? episode, bool movie)
    {
        var clean = title.Trim();

        if (!movie && season is > 0 && episode is > 0)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0} S{1:D2}E{2:D2}",
                clean,
                season.Value,
                episode.Value);
        }

        if (!movie && season is > 0)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0} S{1:D2}", clean, season.Value);
        }

        return year is > 1900 ? $"{clean} {year.Value}" : clean;
    }
}
