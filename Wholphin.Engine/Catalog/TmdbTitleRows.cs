using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Wholphin.Engine.Data;
using Wholphin.Engine.Data.Entities;
using Wholphin.Engine.Data.Enums;
using Wholphin.Engine.Integrations.Jellyseerr;

namespace Wholphin.Engine.Catalog;

/// <summary>
/// TMDB titles as catalog rows, so they can be shown, opened and requested like any other: each
/// (TMDB id, type) matched to its existing row, else imported as a requestable one. More Like This
/// and title search both need this; a title page reads its details and More Like This from the row.
/// </summary>
public static class TmdbTitleRows
{
    /// <summary>
    /// Matches each result to its catalog row, importing a requestable row for the first
    /// <paramref name="maxImports"/> the catalog doesn't have yet. Results are taken in order, so
    /// put the ones that matter most first.
    /// </summary>
    /// <param name="db">The database context; new rows are saved before returning.</param>
    /// <param name="results">TMDB titles, most important first.</param>
    /// <param name="maxImports">The most new rows to add.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The row for each (TMDB id, type) that has one.</returns>
    public static async Task<Dictionary<(int TmdbId, MediaType Type), CatalogItem>> MatchOrImportAsync(
        WholphinDbContext db,
        IReadOnlyList<DiscoverResult> results,
        int maxImports,
        CancellationToken ct)
    {
        var ids = results.Select(r => r.TmdbId).Distinct().ToList();
        var existing = await db.CatalogItems
            .Where(c => c.TmdbId != null && ids.Contains(c.TmdbId.Value))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // The catalog can hold a library copy and a TMDB copy of one title; the library copy wins.
        var rows = existing
            .GroupBy(c => (c.TmdbId!.Value, c.MediaType))
            .ToDictionary(g => g.Key, g => g.Aggregate(Preferred));

        var imported = 0;
        var retitled = 0;
        foreach (var result in results)
        {
            var key = (result.TmdbId, result.MediaType);
            if (rows.TryGetValue(key, out var row))
            {
                // A row outside the library takes TMDB's title. A request creates its row under the
                // title its client sent, and nothing corrected it: one showed Interstellar as "recon-test".
                if (row.JellyfinItemId is null && !string.IsNullOrWhiteSpace(result.Title) && row.Title != result.Title)
                {
                    row.Title = result.Title;
                    retitled++;
                }

                continue;
            }

            if (imported >= maxImports)
            {
                continue;
            }

            // The same shape discovery imports a pick with (PickPersistence).
            var item = new CatalogItem
            {
                TmdbId = result.TmdbId,
                MediaType = result.MediaType,
                Availability = AvailabilityState.Request,
                Title = result.Title,
                Overview = result.Overview,
                ProductionYear = result.Year,
                CommunityRating = result.CommunityRating,
                GenresJson = result.Genres.Count > 0 ? JsonSerializer.Serialize(result.Genres) : null,
                OriginalLanguage = result.OriginalLanguage,
                PosterImageUrl = result.PosterImageUrl,
                BackdropImageUrl = result.BackdropImageUrl,
                LastSyncedAt = DateTime.UtcNow,
            };
            db.CatalogItems.Add(item);
            rows[key] = item;
            imported++;
        }

        if (imported > 0 || retitled > 0)
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false); // gives the new rows their ids
        }

        return rows;
    }

    /// <summary>Of two rows for the same title, the library copy.</summary>
    /// <param name="a">One row.</param>
    /// <param name="b">The other.</param>
    /// <returns>The library row when exactly one is in the library, else <paramref name="a"/>.</returns>
    public static CatalogItem Preferred(CatalogItem a, CatalogItem b) =>
        (b.JellyfinItemId is not null && a.JellyfinItemId is null) ? b : a;
}
