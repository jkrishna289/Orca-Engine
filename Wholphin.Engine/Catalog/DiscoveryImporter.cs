using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wholphin.Engine.Data;
using Wholphin.Engine.Data.Entities;
using Wholphin.Engine.Data.Enums;
using Wholphin.Engine.Integrations.Jellyseerr;
using Wholphin.Engine.Integrations.Tmdb;
using Wholphin.Engine.Settings;

namespace Wholphin.Engine.Catalog;

/// <summary>
/// Pulls requestable (not-yet-available) titles into the catalog from the hybrid discovery sources —
/// Jellyseerr's discover endpoints and (when configured) TMDB-direct trending/popular. Only adds
/// titles NOT already in the catalog (matched by TMDB id) so it never clobbers a richer library-synced
/// row, and skips anything already available. TMDB results arrive with genre names + artwork already
/// resolved; Jellyseerr-only rows are later filled in by the <see cref="ICatalogEnricher"/>.
/// </summary>
public class DiscoveryImporter : IDiscoveryImporter
{
    private static readonly MediaType[] DiscoverTypes = { MediaType.Movie, MediaType.Series };

    private static readonly TmdbDiscoverCategory[] TmdbCategories =
    {
        TmdbDiscoverCategory.Trending,
        TmdbDiscoverCategory.Popular,
    };

    private readonly IJellyseerrClient _jellyseerr;
    private readonly ITmdbClient _tmdb;
    private readonly ISettingsService _settings;
    private readonly IWholphinDbContextFactory _factory;
    private readonly ILogger<DiscoveryImporter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DiscoveryImporter"/> class.
    /// </summary>
    /// <param name="jellyseerr">The Jellyseerr client.</param>
    /// <param name="tmdb">The TMDB client (second hybrid discovery source).</param>
    /// <param name="settings">The layered settings resolver.</param>
    /// <param name="factory">The database context factory.</param>
    /// <param name="logger">The logger.</param>
    public DiscoveryImporter(
        IJellyseerrClient jellyseerr,
        ITmdbClient tmdb,
        ISettingsService settings,
        IWholphinDbContextFactory factory,
        ILogger<DiscoveryImporter> logger)
    {
        _jellyseerr = jellyseerr;
        _tmdb = tmdb;
        _settings = settings;
        _factory = factory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<int> ImportAsync(int maxPagesPerType = 1, CancellationToken ct = default)
    {
        var settings = await _settings.ResolveAsync(null, ct).ConfigureAwait(false);
        var useJellyseerr = settings.Features.JellyseerrDiscovery && _jellyseerr.IsConfigured;
        var useTmdb = settings.Features.TmdbDiscovery && _tmdb.IsConfigured;
        if (!useJellyseerr && !useTmdb)
        {
            return 0;
        }

        var pages = Math.Clamp(maxPagesPerType, 1, 20);

        await using var db = _factory.Create();
        var known = (await db.CatalogItems
                .Where(c => c.TmdbId != null)
                .Select(c => c.TmdbId!.Value)
                .ToListAsync(ct)
                .ConfigureAwait(false))
            .ToHashSet();

        var added = 0;
        foreach (var type in DiscoverTypes)
        {
            if (useJellyseerr)
            {
                for (var page = 1; page <= pages; page++)
                {
                    var results = await _jellyseerr.DiscoverAsync(type, page, ct).ConfigureAwait(false);
                    if (results.Count == 0)
                    {
                        break;
                    }

                    added += AddNew(db, results, known);
                }
            }

            if (useTmdb)
            {
                foreach (var category in TmdbCategories)
                {
                    for (var page = 1; page <= pages; page++)
                    {
                        var results = await _tmdb.DiscoverAsync(type, category, page, ct).ConfigureAwait(false);
                        if (results.Count == 0)
                        {
                            break;
                        }

                        added += AddNew(db, results, known);
                    }
                }
            }
        }

        if (added > 0)
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        _logger.LogInformation("Orca Engine: discovery import added {Count} requestable items.", added);
        return added;
    }

    private static int AddNew(WholphinDbContext db, IReadOnlyList<DiscoverResult> results, HashSet<int> known)
    {
        var added = 0;
        foreach (var r in results)
        {
            // Skip: invalid ids; already-available titles (handled by library sync); blacklisted titles
            // (Unavailable); and anything already in the catalog (matched by TMDB id, across all sources).
            if (r.TmdbId <= 0
                || r.Availability is AvailabilityState.WatchNow or AvailabilityState.Unavailable
                || !known.Add(r.TmdbId))
            {
                continue;
            }

            db.CatalogItems.Add(new CatalogItem
            {
                TmdbId = r.TmdbId,
                MediaType = r.MediaType,
                Availability = r.Availability,
                Title = r.Title,
                Overview = r.Overview,
                ProductionYear = r.Year,
                CommunityRating = r.CommunityRating,
                GenresJson = r.Genres.Count > 0 ? JsonSerializer.Serialize(r.Genres) : null,
                PosterImageUrl = r.PosterImageUrl,
                BackdropImageUrl = r.BackdropImageUrl,
                TrailerUrl = r.TrailerUrl,
                LastSyncedAt = DateTime.UtcNow,
            });
            added++;
        }

        return added;
    }
}
