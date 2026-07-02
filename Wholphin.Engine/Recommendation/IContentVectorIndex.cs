using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wholphin.Engine.Embedding;

namespace Wholphin.Engine.Recommendation;

/// <summary>
/// An immutable snapshot of the catalog's content vectors: one <see cref="ContentVector"/> per indexed
/// catalog item, produced by the active <see cref="IEmbeddingProvider"/>. Cheap repeated cosine
/// lookups without re-embedding.
/// </summary>
public sealed class ContentVectorSnapshot
{
    private readonly IReadOnlyDictionary<long, ContentVector> _vectors;

    /// <summary>Initializes a new instance of the <see cref="ContentVectorSnapshot"/> class.</summary>
    /// <param name="vectors">Vectors keyed by catalog item id.</param>
    /// <param name="providerName">The embedding provider that produced these vectors.</param>
    public ContentVectorSnapshot(IReadOnlyDictionary<long, ContentVector> vectors, string providerName)
    {
        _vectors = vectors;
        ProviderName = providerName;
    }

    /// <summary>Gets the number of indexed items.</summary>
    public int Count => _vectors.Count;

    /// <summary>Gets the embedding provider that produced this snapshot.</summary>
    public string ProviderName { get; }

    /// <summary>Returns the vector for an indexed item, or <see cref="ContentVector.Empty"/> if absent.</summary>
    /// <param name="catalogItemId">The catalog item id.</param>
    /// <returns>The content vector.</returns>
    public ContentVector VectorFor(long catalogItemId)
        => _vectors.TryGetValue(catalogItemId, out var vector) ? vector : ContentVector.Empty;
}

/// <summary>
/// Builds and caches the catalog-wide content-vector index via the active embedding provider
/// (TF-IDF by default; a cloud or local-ONNX model when configured). Recomputed lazily on a TTL.
/// Tier-1 (in-memory) today; swappable for a persisted vector store behind this same port later.
/// </summary>
public interface IContentVectorIndex
{
    /// <summary>Gets the current content-vector snapshot (cached; rebuilt on a TTL or provider change).</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The snapshot.</returns>
    Task<ContentVectorSnapshot> GetAsync(CancellationToken ct = default);
}
