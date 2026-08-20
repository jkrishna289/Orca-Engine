using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wholphin.Engine.Data;
using Wholphin.Engine.Data.Enums;
using Wholphin.Engine.Diagnostics;
using Wholphin.Engine.Integrations.Tmdb;

namespace Wholphin.Engine.Catalog;

/// <summary>
/// Default <see cref="ICatalogEnricher"/> backed by <see cref="ITmdbClient"/>. Selects requestable
/// rows (no Jellyfin id, but a known TMDB id) that are missing genres or poster art and fills them
/// in from a TMDB detail lookup — so discovery cards ("You Might Like" etc.) show real artwork and
/// request affinity/similarity get genre signal for not-yet-available titles.
/// </summary>
/// <remarks>
/// Library rows are in scope for exactly one field: the original language, which Jellyfin does not
/// supply and which nothing else could fill when the multi-provider layer is switched off. They are
/// deliberately NOT in scope for artwork — a library title renders from its Jellyfin id, and writing
/// a TMDB poster onto it would override the art the server already has.
/// </remarks>
public class TmdbEnricher : ICatalogEnricher
{
    private static readonly MediaType[] Enrichable = { MediaType.Movie, MediaType.Series };

    private readonly ITmdbClient _tmdb;
    private readonly IWholphinDbContextFactory _factory;
    private readonly IEngineMetrics _metrics;
    private readonly ILogger<TmdbEnricher> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TmdbEnricher"/> class.
    /// </summary>
    /// <param name="tmdb">The TMDB client.</param>
    /// <param name="factory">The database context factory.</param>
    /// <param name="metrics">Operational metrics.</param>
    /// <param name="logger">The logger.</param>
    public TmdbEnricher(ITmdbClient tmdb, IWholphinDbContextFactory factory, IEngineMetrics metrics, ILogger<TmdbEnricher> logger)
    {
        _tmdb = tmdb;
        _factory = factory;
        _metrics = metrics;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<int> EnrichAsync(int maxItems = 50, CancellationToken ct = default)
    {
        if (!_tmdb.IsConfigured)
        {
            return 0;
        }

        var limit = Math.Clamp(maxItems, 1, 500);

        await using var db = _factory.Create();
        var candidates = await db.CatalogItems
            .Where(c => c.TmdbId != null
                        && (c.OriginalLanguage == null
                            || (c.JellyfinItemId == null
                                && (c.GenresJson == null || c.PosterImageUrl == null))))
            .OrderBy(c => c.LastSyncedAt)
            .Take(limit)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (candidates.Count == 0)
        {
            return 0;
        }

        var enriched = 0;
        foreach (var item in candidates)
        {
            ct.ThrowIfCancellationRequested();

            if (!Enrichable.Contains(item.MediaType))
            {
                continue;
            }

            var e = await _tmdb.EnrichAsync(item.TmdbId!.Value, item.MediaType, ct).ConfigureAwait(false);

            // Touch the cursor regardless of outcome so a title TMDB can't enrich rotates to the back
            // instead of being re-fetched every pass.
            item.LastSyncedAt = DateTime.UtcNow;

            if (e is null)
            {
                continue;
            }

            var changed = false;
            if (e.Genres.Count > 0 && string.IsNullOrEmpty(item.GenresJson))
            {
                item.GenresJson = JsonSerializer.Serialize(e.Genres);
                changed = true;
            }

            if (e.Keywords.Count > 0 && string.IsNullOrEmpty(item.TagsJson))
            {
                item.TagsJson = JsonSerializer.Serialize(e.Keywords);
                changed = true;
            }

            if (e.People.Count > 0 && string.IsNullOrEmpty(item.PeopleJson))
            {
                item.PeopleJson = JsonSerializer.Serialize(e.People);
                changed = true;
            }

            // Requestable rows only — see the note on the class about library artwork.
            if (item.JellyfinItemId is null)
            {
                if (!string.IsNullOrEmpty(e.PosterImageUrl) && string.IsNullOrEmpty(item.PosterImageUrl))
                {
                    item.PosterImageUrl = e.PosterImageUrl;
                    changed = true;
                }

                if (!string.IsNullOrEmpty(e.BackdropImageUrl) && string.IsNullOrEmpty(item.BackdropImageUrl))
                {
                    item.BackdropImageUrl = e.BackdropImageUrl;
                    changed = true;
                }
            }

            if (!string.IsNullOrEmpty(e.TrailerUrl) && string.IsNullOrEmpty(item.TrailerUrl))
            {
                item.TrailerUrl = e.TrailerUrl;
                changed = true;
            }

            if (string.IsNullOrEmpty(item.Overview) && !string.IsNullOrEmpty(e.Overview))
            {
                item.Overview = e.Overview;
            }

            if (item.ProductionYear is null && e.Year is not null)
            {
                item.ProductionYear = e.Year;
            }

            if (item.CommunityRating is null && e.CommunityRating is not null)
            {
                item.CommunityRating = e.CommunityRating;
            }

            if (item.RuntimeMinutes is null && e.RuntimeMinutes is not null)
            {
                item.RuntimeMinutes = e.RuntimeMinutes;
            }

            if (string.IsNullOrEmpty(item.OriginalLanguage) && !string.IsNullOrEmpty(e.OriginalLanguage))
            {
                item.OriginalLanguage = e.OriginalLanguage;
                changed = true;
            }

            if (string.IsNullOrEmpty(item.CollectionName) && !string.IsNullOrEmpty(e.CollectionName))
            {
                item.CollectionName = e.CollectionName;
            }

            if (changed)
            {
                enriched++;
            }
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        if (enriched > 0)
        {
            _metrics.Increment("tmdb.enrich.items", enriched);
        }

        _logger.LogInformation("Orca Engine: TMDB enrichment filled {Count} of {Scanned} candidate rows.", enriched, candidates.Count);
        return enriched;
    }
}
