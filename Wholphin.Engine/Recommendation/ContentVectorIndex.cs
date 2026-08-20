using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wholphin.Engine.Caching;
using Wholphin.Engine.Data;
using Wholphin.Engine.Data.Enums;
using Wholphin.Engine.Diagnostics;
using Wholphin.Engine.Embedding;

namespace Wholphin.Engine.Recommendation;

/// <summary>
/// Default <see cref="IContentVectorIndex"/>: builds a canonical text document per non-unavailable
/// catalog item, reuses whatever the <see cref="VectorStore"/> already holds for it, embeds only the
/// rest in bounded batches, and caches the assembled snapshot.
/// </summary>
/// <remarks>
/// <para>
/// <b>Rebuilds are incremental, and that falls out of persistence rather than being bolted on.</b>
/// A stored vector carries the hash of the document it was made from, so "is this still valid?" and
/// "have I got one?" are the same question. A restart re-embeds nothing; adding ten titles embeds
/// ten; editing a title's metadata re-embeds exactly that title. Only a provider or model change
/// invalidates everything, which it must.
/// </para>
/// <para>
/// <b>A failed rebuild must never cost you the index you already had.</b> The naive shape — build
/// into the cache, cache whatever came back — writes an EMPTY snapshot on failure and then serves it
/// for the full TTL, so one bad afternoon silently turns off content similarity until tomorrow. Here
/// the new index is assembled off to the side and only becomes visible once it is complete; a failed
/// build keeps the previous one and retries sooner.
/// </para>
/// <para>
/// Concurrent callers coalesce through <see cref="SingleFlight"/>: on a cold cache, N simultaneous
/// requests would otherwise each rebuild the entire catalog.
/// </para>
/// </remarks>
public class ContentVectorIndex : IContentVectorIndex
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(6);

    /// <summary>
    /// How long a failed rebuild is remembered before the next attempt. Far shorter than
    /// <see cref="CacheTtl"/> — a transient provider outage should cost minutes of staleness, not
    /// the six hours a successful index is trusted for.
    /// </summary>
    private static readonly TimeSpan FailedRebuildRetryTtl = TimeSpan.FromMinutes(10);

    /// <summary>Batches between progress log lines. Per-batch logging on a large catalog is noise.</summary>
    private const int LogEveryBatches = 10;

    private readonly IWholphinDbContextFactory _factory;
    private readonly ICache _cache;
    private readonly IEmbeddingService _embeddings;
    private readonly VectorStore _store;
    private readonly IEngineMetrics _metrics;
    private readonly ILogger<ContentVectorIndex> _logger;

    /// <summary>
    /// The last index that built cleanly, kept so a failed rebuild has something to fall back to
    /// within this process. Written as a whole reference, never mutated in place, so a reader either
    /// sees the complete old index or the complete new one and never a half-populated dictionary.
    /// </summary>
    private ContentVectorSnapshot? _lastGood;

    /// <summary>Initializes a new instance of the <see cref="ContentVectorIndex"/> class.</summary>
    /// <param name="factory">The database context factory.</param>
    /// <param name="cache">The L1 cache.</param>
    /// <param name="embeddings">The embedding service (provider selection, batching, retries).</param>
    /// <param name="store">Durable vector storage, so restarts do not re-embed the catalog.</param>
    /// <param name="metrics">Operational counters.</param>
    /// <param name="logger">The logger.</param>
    public ContentVectorIndex(
        IWholphinDbContextFactory factory,
        ICache cache,
        IEmbeddingService embeddings,
        VectorStore store,
        IEngineMetrics metrics,
        ILogger<ContentVectorIndex> logger)
    {
        _factory = factory;
        _cache = cache;
        _embeddings = embeddings;
        _store = store;
        _metrics = metrics;
        _logger = logger;
    }

    /// <inheritdoc />
    public ContentVectorSnapshot? Current => _lastGood;

    /// <inheritdoc />
    public async Task<ContentVectorSnapshot> GetAsync(CancellationToken ct = default)
    {
        var providerName = _embeddings.ActiveProviderName;

        var snapshot = await SingleFlight.GetOrCreateAsync<ContentVectorSnapshot>(
            _cache,
            $"contentvectors:{providerName}",
            CacheTtl,
            FailedRebuildRetryTtl,
            token => BuildAsync(providerName, token),
            ct).ConfigureAwait(false);

        if (snapshot is not null)
        {
            _lastGood = snapshot;
            return snapshot;
        }

        // The rebuild failed. Serving the previous index is strictly better than serving nothing:
        // slightly stale vectors still rank; an empty index silently scores everything at zero.
        var lastGood = _lastGood;
        if (lastGood is not null)
        {
            _metrics.Increment("embedding.index.served_stale");
            return lastGood;
        }

        return ContentVectorSnapshot.Empty(providerName);
    }

    /// <summary>
    /// Builds a complete index, or returns null if it could not.
    /// </summary>
    /// <remarks>
    /// Null rather than a partial snapshot, deliberately. A snapshot missing some items is
    /// indistinguishable at the call site from one where those items genuinely have no similarity,
    /// so publishing one would turn a loud provider outage into a quiet ranking bug.
    /// </remarks>
    private async Task<ContentVectorSnapshot?> BuildAsync(string providerName, CancellationToken ct)
    {
        long[] ids;
        string[] documents;

        // Scoped so the entity list is collectible before the vectors are allocated — the two
        // together are the peak, and they do not need to overlap.
        await using (var db = _factory.Create())
        {
            var items = await db.CatalogItems.AsNoTracking()
                .Where(c => c.Availability != AvailabilityState.Unavailable)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            ids = new long[items.Count];
            documents = new string[items.Count];
            for (var i = 0; i < items.Count; i++)
            {
                ids[i] = items[i].Id;
                documents[i] = ContentDocument.Of(items[i]);
            }
        }

        if (documents.Length == 0)
        {
            return ContentVectorSnapshot.Empty(providerName);
        }

        var modelId = _embeddings.ActiveModelId;
        var stored = await _store.LoadAsync(providerName, modelId, ct).ConfigureAwait(false);

        var vectorsByItem = new Dictionary<long, ContentVector>(ids.Length);
        var missIds = new List<long>();
        var missDocuments = new List<string>();
        var missHashes = new List<string>();

        for (var i = 0; i < ids.Length; i++)
        {
            var hash = VectorStore.HashDocument(documents[i]);

            // Same item, same provider, same model, same text — the stored vector is exactly what a
            // fresh call would return, so calling again would buy nothing and cost money or seconds.
            if (stored.TryGetValue(ids[i], out var hit) && hit.Hash == hash && !hit.Vector.IsEmpty)
            {
                vectorsByItem[ids[i]] = hit.Vector;
                continue;
            }

            missIds.Add(ids[i]);
            missDocuments.Add(documents[i]);
            missHashes.Add(hash);
        }

        _metrics.Increment("embedding.index.reused", vectorsByItem.Count);

        if (missDocuments.Count == 0)
        {
            _logger.LogInformation(
                "Orca Engine: content vector index served entirely from storage — {Count} items via '{Provider}'/{Model}.",
                vectorsByItem.Count,
                providerName,
                modelId);
            return new ContentVectorSnapshot(vectorsByItem, providerName);
        }

        _logger.LogInformation(
            "Orca Engine: content vector index needs {Missing} of {Total} items embedded ('{Provider}'/{Model}).",
            missDocuments.Count,
            ids.Length,
            providerName,
            modelId);

        var run = await _embeddings.EmbedCorpusAsync(missDocuments, LogProgress(), ct).ConfigureAwait(false);
        if (!run.Succeeded)
        {
            _metrics.Increment("embedding.index.build.error");
            _logger.LogWarning(
                "Orca Engine: content vector index rebuild failed after {Completed} batch(es) — {Failure}. Keeping the previous index.",
                run.BatchesCompleted,
                run.Failure ?? "no vectors produced");
            return null;
        }

        var written = new List<(long ItemId, string Hash, ContentVector Vector)>(missIds.Count);
        for (var i = 0; i < missIds.Count; i++)
        {
            // Positional alignment is the contract EmbedCorpusAsync guarantees, and the three miss
            // lists were appended to in one pass, so index i means the same item in all of them.
            vectorsByItem[missIds[i]] = run.Vectors[i];
            written.Add((missIds[i], missHashes[i], run.Vectors[i]));
        }

        var saved = await _store.SaveAsync(providerName, modelId, written, ids, ct).ConfigureAwait(false);
        _metrics.Increment("embedding.index.persisted", saved);
        _metrics.Increment("embedding.index.build.ok");

        _logger.LogInformation(
            "Orca Engine: content vector index built — {Count} items via '{Provider}' ({Embedded} newly embedded, {Reused} reused from storage).",
            vectorsByItem.Count,
            run.ProviderName,
            written.Count,
            vectorsByItem.Count - written.Count);

        return new ContentVectorSnapshot(vectorsByItem, providerName);
    }

    private IProgress<EmbeddingProgress> LogProgress() => new Progress<EmbeddingProgress>(p =>
    {
        if (p.BatchesCompleted % LogEveryBatches != 0 && p.BatchesCompleted != p.BatchesTotal)
        {
            return;
        }

        _logger.LogInformation(
            "Orca Engine: embedding {Done}/{Total} batches ({Embedded}/{Documents} documents) via '{Provider}'.",
            p.BatchesCompleted,
            p.BatchesTotal,
            p.DocumentsEmbedded,
            p.DocumentCount,
            p.ProviderName);
    });
}
