using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Wholphin.Engine.Data;
using Wholphin.Engine.Data.Entities;
using Wholphin.Engine.Embedding;

namespace Wholphin.Engine.Recommendation;

/// <summary>
/// Durable storage for catalog content vectors, keyed by item and stamped with the provider, model
/// and document hash that produced them.
/// </summary>
/// <remarks>
/// Embeddings are the one thing the engine computes that is genuinely expensive — a hosted provider
/// charges for them and a local one spends real seconds per batch — and until this existed the index
/// lived only in memory, so every restart threw the whole catalog away and paid for it again.
/// </remarks>
public sealed class VectorStore
{
    /// <summary>Rows written per SaveChanges. Bounded so a full catalog write does not build one giant transaction.</summary>
    private const int WriteBatchSize = 500;

    private readonly IWholphinDbContextFactory _factory;

    /// <summary>Initializes a new instance of the <see cref="VectorStore"/> class.</summary>
    /// <param name="factory">The database context factory.</param>
    public VectorStore(IWholphinDbContextFactory factory) => _factory = factory;

    /// <summary>Hashes a content document, so a changed document invalidates its stored vector.</summary>
    /// <param name="document">The document text.</param>
    /// <returns>A hex SHA-256 digest.</returns>
    public static string HashDocument(string document) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(document)));

    /// <summary>
    /// Loads every stored vector that the given provider and model produced, keyed by item id.
    /// </summary>
    /// <param name="provider">The active provider name.</param>
    /// <param name="modelId">The active model id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Item id to (document hash, vector).</returns>
    /// <remarks>
    /// Rows from any other provider or model are not merely skipped — they are deleted, because they
    /// can never become valid again and would otherwise accumulate a copy of the catalog per model
    /// the operator has ever tried.
    /// </remarks>
    public async Task<Dictionary<long, (string Hash, ContentVector Vector)>> LoadAsync(
        string provider,
        string modelId,
        CancellationToken ct = default)
    {
        await using var db = _factory.Create();

        await db.CatalogItemVectors
            .Where(v => v.Provider != provider || v.ModelId != modelId)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);

        var rows = await db.CatalogItemVectors.AsNoTracking()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var loaded = new Dictionary<long, (string, ContentVector)>(rows.Count);
        foreach (var row in rows)
        {
            // A blob whose length disagrees with its own Dimensions column was truncated or written
            // by a different build; treat it as absent rather than reconstructing nonsense.
            if (row.Vector.Length != row.Dimensions * sizeof(float) || row.Dimensions <= 0)
            {
                continue;
            }

            loaded[row.CatalogItemId] = (row.DocumentHash, ContentVector.Dense(ToFloats(row.Vector)));
        }

        return loaded;
    }

    /// <summary>
    /// Replaces the stored vectors for the given items, and drops rows for items no longer indexed.
    /// </summary>
    /// <param name="provider">The provider that produced them.</param>
    /// <param name="modelId">The model that produced them.</param>
    /// <param name="written">The item id, document hash and vector for each item to store.</param>
    /// <param name="liveItemIds">Every item currently in the index; anything else is pruned.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>How many rows were written.</returns>
    public async Task<int> SaveAsync(
        string provider,
        string modelId,
        IReadOnlyList<(long ItemId, string Hash, ContentVector Vector)> written,
        IReadOnlyCollection<long> liveItemIds,
        CancellationToken ct = default)
    {
        await using var db = _factory.Create();

        // Items deleted from the library leave vectors behind; without this the table only grows.
        var live = liveItemIds.ToHashSet();
        var orphans = await db.CatalogItemVectors.AsNoTracking()
            .Select(v => v.CatalogItemId)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var toDrop = orphans.Where(id => !live.Contains(id)).ToList();

        foreach (var chunk in Chunk(toDrop, WriteBatchSize))
        {
            await db.CatalogItemVectors
                .Where(v => chunk.Contains(v.CatalogItemId))
                .ExecuteDeleteAsync(ct)
                .ConfigureAwait(false);
        }

        var now = DateTime.UtcNow;
        var saved = 0;

        foreach (var chunk in Chunk(written, WriteBatchSize))
        {
            ct.ThrowIfCancellationRequested();

            // Upsert by delete-then-insert: the set being replaced is exactly what was just
            // re-embedded, and SQLite has no portable EF upsert.
            var ids = chunk.Select(w => w.ItemId).ToList();
            await db.CatalogItemVectors
                .Where(v => ids.Contains(v.CatalogItemId))
                .ExecuteDeleteAsync(ct)
                .ConfigureAwait(false);

            foreach (var (itemId, hash, vector) in chunk)
            {
                if (vector.DenseValues is not { Count: > 0 } values)
                {
                    continue;
                }

                db.CatalogItemVectors.Add(new CatalogItemVector
                {
                    CatalogItemId = itemId,
                    Provider = provider,
                    ModelId = modelId,
                    Dimensions = values.Count,
                    DocumentHash = hash,
                    Vector = ToBytes(values),
                    UpdatedAt = now,
                });
                saved++;
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            db.ChangeTracker.Clear();
        }

        return saved;
    }

    /// <summary>Counts the stored vectors, for diagnostics.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The row count.</returns>
    public async Task<int> CountAsync(CancellationToken ct = default)
    {
        await using var db = _factory.Create();
        return await db.CatalogItemVectors.AsNoTracking().CountAsync(ct).ConfigureAwait(false);
    }

    // Machine-native float layout. The database moving between machines of different endianness
    // would invalidate these blobs; the Dimensions check catches a truncated read, not a byte-swapped
    // one. Acceptable for a file that lives beside the Jellyfin install it was written by.
    private static byte[] ToBytes(IReadOnlyList<float> values)
    {
        var bytes = new byte[values.Count * sizeof(float)];
        for (var i = 0; i < values.Count; i++)
        {
            BitConverter.TryWriteBytes(bytes.AsSpan(i * sizeof(float)), values[i]);
        }

        return bytes;
    }

    private static float[] ToFloats(byte[] bytes)
    {
        var values = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);
        return values;
    }

    private static IEnumerable<List<T>> Chunk<T>(IReadOnlyList<T> source, int size)
    {
        for (var start = 0; start < source.Count; start += size)
        {
            yield return source.Skip(start).Take(size).ToList();
        }
    }
}
