using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Wholphin.Engine.Caching;
using Wholphin.Engine.Data;
using Wholphin.Engine.Data.Enums;
using Wholphin.Engine.Personalization;

namespace Wholphin.Engine.Recommendation;

/// <summary>
/// Builds and caches the genre relationship graph from the catalog's genre co-occurrence.
/// Recomputed lazily on a TTL; cheap at the self-hosted catalog scale.
/// </summary>
public class GenreGraphService : IGenreGraphService
{
    private const string CacheKeyName = "genregraph";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(6);

    private readonly IWholphinDbContextFactory _factory;
    private readonly ICache _cache;

    /// <summary>
    /// Initializes a new instance of the <see cref="GenreGraphService"/> class.
    /// </summary>
    /// <param name="factory">The database context factory.</param>
    /// <param name="cache">The L1 cache.</param>
    public GenreGraphService(IWholphinDbContextFactory factory, ICache cache)
    {
        _factory = factory;
        _cache = cache;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GenreLink>> GetRelatedAsync(string genre, int limit, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(genre))
        {
            return Array.Empty<GenreLink>();
        }

        var graph = await GetGraphAsync(ct).ConfigureAwait(false);
        return graph.TryGetValue(genre, out var links)
            ? links.Take(Math.Clamp(limit, 1, 50)).ToList()
            : Array.Empty<GenreLink>();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, List<GenreLink>>> GetGraphAsync(CancellationToken ct = default)
    {
        if (_cache.TryGet<Dictionary<string, List<GenreLink>>>(CacheKeyName, out var cached) && cached is not null)
        {
            return cached;
        }

        await using var db = _factory.Create();
        var genreJsons = await db.CatalogItems.AsNoTracking()
            .Where(c => c.Availability != AvailabilityState.Unavailable && c.GenresJson != null)
            .Select(c => c.GenresJson!)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var itemGenres = genreJsons.Select(j => CatalogFeatures.Parse(j)).ToList();
        var graph = GenreGraph.Build(itemGenres);

        _cache.Set(CacheKeyName, graph, CacheTtl);
        return graph;
    }
}
