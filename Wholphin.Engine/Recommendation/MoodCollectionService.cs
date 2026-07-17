using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Wholphin.Engine.Caching;
using Wholphin.Engine.Data;
using Wholphin.Engine.Data.Entities;
using Wholphin.Engine.Data.Enums;
using Wholphin.Engine.Llm;
using Wholphin.Engine.Personalization;

namespace Wholphin.Engine.Recommendation;

/// <summary>
/// Default <see cref="IMoodCollectionService"/>. Applies the rotating <see cref="MoodCollections"/>
/// predicates over the available catalog, ranks each mood's matches by rating, and emits the rows that
/// clear a minimum size. Under LLM curation the predicate match only prefilters a wider pool and the
/// LLM picks the titles that genuinely carry the mood (fail-soft to the rating order). Cached briefly
/// so repeated home builds within a window reuse the work.
/// </summary>
public class MoodCollectionService : IMoodCollectionService
{
    private const string CacheKeyPrefix = "mood";
    private const int MaxMoodRows = 3;
    private const int MinItemsPerMood = 4;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan LlmMoodCacheTtl = TimeSpan.FromHours(24);

    private readonly IWholphinDbContextFactory _factory;
    private readonly ICache _cache;
    private readonly ILlmReRanker _llm;

    /// <summary>
    /// Initializes a new instance of the <see cref="MoodCollectionService"/> class.
    /// </summary>
    public MoodCollectionService(IWholphinDbContextFactory factory, ICache cache, ILlmReRanker llm)
    {
        _factory = factory;
        _cache = cache;
        _llm = llm;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MoodRow>> BuildAsync(int rowSize, CancellationToken ct = default)
    {
        if (rowSize <= 0)
        {
            return Array.Empty<MoodRow>();
        }

        var day = DateTime.UtcNow.DayOfYear;
        var cacheKey = $"{CacheKeyPrefix}:{day}:{rowSize}";
        if (_cache.TryGet<List<MoodRow>>(cacheKey, out var cached) && cached is not null)
        {
            return cached;
        }

        await using var db = _factory.Create();
        var items = await db.CatalogItems
            .Where(c => c.Availability != AvailabilityState.Unavailable)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // Precompute the feature view once per item (genres/tags are parsed from JSON).
        var featured = items
            .Select(item => (Item: item, Features: ToFeatures(item)))
            .ToList();

        var rows = new List<MoodRow>();
        foreach (var mood in MoodCollections.Rotate(day, MaxMoodRows))
        {
            // A wide predicate pool: the LLM (when curating) picks the titles that genuinely carry
            // the mood; otherwise the rating cut below is the row.
            var pool = featured
                .Where(f => mood.Match(f.Features))
                .Select(f => f.Item)
                .OrderByDescending(i => i.CommunityRating ?? 0f)
                .ThenByDescending(i => i.DateAdded ?? DateTime.MinValue)
                .Take(rowSize * 3)
                .ToList();

            var curated = await _llm.CurateAsync(
                new LlmCurationRequest
                {
                    Purpose = "mood",
                    Pool = pool,
                    Count = rowSize,
                    Context = $"a themed collection row titled '{mood.Title}' — keep only titles that truly fit that mood",
                    CacheTtl = LlmMoodCacheTtl,
                },
                ct).ConfigureAwait(false);

            var matches = curated.Items.Take(rowSize).ToList();
            if (matches.Count >= MinItemsPerMood)
            {
                rows.Add(new MoodRow(mood.Id, mood.Title, matches));
            }
        }

        _cache.Set(cacheKey, rows, CacheTtl);
        return rows;
    }

    private static MoodFeatures ToFeatures(CatalogItem item)
    {
        var genres = new HashSet<string>(CatalogFeatures.Parse(item.GenresJson), StringComparer.OrdinalIgnoreCase);
        var tags = new HashSet<string>(CatalogFeatures.Parse(item.TagsJson), StringComparer.OrdinalIgnoreCase);
        return new MoodFeatures(genres, tags, item.MediaType, item.RuntimeMinutes, item.CommunityRating);
    }
}
