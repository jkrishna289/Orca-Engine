using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Wholphin.Engine.Caching;
using Wholphin.Engine.Data;
using Wholphin.Engine.Data.Enums;
using Wholphin.Engine.Embedding;

namespace Wholphin.Engine.Recommendation;

/// <summary>
/// Default <see cref="IContentVectorIndex"/>: builds a canonical text document per non-unavailable
/// catalog item, embeds the batch through the active <see cref="IEmbeddingService"/>, and caches the
/// resulting vectors. The cache key includes the provider name, so switching providers (e.g. TF-IDF →
/// a cloud model) rebuilds rather than serving stale vectors.
/// </summary>
public class ContentVectorIndex : IContentVectorIndex
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(6);

    private readonly IWholphinDbContextFactory _factory;
    private readonly ICache _cache;
    private readonly IEmbeddingService _embeddings;

    /// <summary>Initializes a new instance of the <see cref="ContentVectorIndex"/> class.</summary>
    /// <param name="factory">The database context factory.</param>
    /// <param name="cache">The L1 cache.</param>
    /// <param name="embeddings">The embedding service (provider selection + fallback).</param>
    public ContentVectorIndex(IWholphinDbContextFactory factory, ICache cache, IEmbeddingService embeddings)
    {
        _factory = factory;
        _cache = cache;
        _embeddings = embeddings;
    }

    /// <inheritdoc />
    public async Task<ContentVectorSnapshot> GetAsync(CancellationToken ct = default)
    {
        var providerName = _embeddings.ActiveProviderName;
        var cacheKey = $"contentvectors:{providerName}";
        if (_cache.TryGet<ContentVectorSnapshot>(cacheKey, out var cached) && cached is not null)
        {
            return cached;
        }

        await using var db = _factory.Create();
        var items = await db.CatalogItems.AsNoTracking()
            .Where(c => c.Availability != AvailabilityState.Unavailable)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var documents = new List<string>(items.Count);
        foreach (var item in items)
        {
            documents.Add(ContentDocument.Of(item));
        }

        var vectorsByItem = new Dictionary<long, ContentVector>(items.Count);
        var embedded = await _embeddings.EmbedAsync(documents, ct).ConfigureAwait(false);
        if (embedded is not null && embedded.Count == items.Count)
        {
            for (var i = 0; i < items.Count; i++)
            {
                vectorsByItem[items[i].Id] = embedded[i];
            }
        }

        var snapshot = new ContentVectorSnapshot(vectorsByItem, providerName);
        _cache.Set(cacheKey, snapshot, CacheTtl);
        return snapshot;
    }
}
