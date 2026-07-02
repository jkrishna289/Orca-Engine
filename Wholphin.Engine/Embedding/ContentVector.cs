using System;
using System.Collections.Generic;
using Wholphin.Engine.Recommendation;

namespace Wholphin.Engine.Embedding;

/// <summary>
/// A unified content vector that is either <em>sparse</em> (TF-IDF term→weight, high-dimensional) or
/// <em>dense</em> (a fixed-length neural embedding). Both are stored L2-normalized so cosine
/// similarity is just the dot product. This lets the recommendation layer treat every
/// <see cref="IEmbeddingProvider"/> the same, whether local TF-IDF or a hosted embedding model.
/// </summary>
public sealed class ContentVector
{
    /// <summary>An empty vector (cosine 0 against anything).</summary>
    public static readonly ContentVector Empty = new(null, null);

    private readonly IReadOnlyDictionary<string, double>? _sparse;
    private readonly float[]? _dense;

    private ContentVector(IReadOnlyDictionary<string, double>? sparse, float[]? dense)
    {
        _sparse = sparse;
        _dense = dense;
    }

    /// <summary>Gets a value indicating whether this vector carries no signal.</summary>
    public bool IsEmpty =>
        (_sparse is null || _sparse.Count == 0) && (_dense is null || _dense.Length == 0);

    /// <summary>Wraps an already-normalized sparse TF-IDF vector.</summary>
    /// <param name="weights">The term→weight map (assumed L2-normalized).</param>
    /// <returns>The content vector.</returns>
    public static ContentVector Sparse(IReadOnlyDictionary<string, double> weights)
        => weights.Count == 0 ? Empty : new ContentVector(weights, null);

    /// <summary>Wraps a dense embedding, L2-normalizing it so cosine reduces to a dot product.</summary>
    /// <param name="values">The raw embedding values.</param>
    /// <returns>The content vector (empty when the input is empty or zero).</returns>
    public static ContentVector Dense(float[] values)
    {
        if (values.Length == 0)
        {
            return Empty;
        }

        var sumSquares = 0.0;
        foreach (var v in values)
        {
            sumSquares += (double)v * v;
        }

        if (sumSquares <= 0)
        {
            return Empty;
        }

        var norm = Math.Sqrt(sumSquares);
        var normalized = new float[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            normalized[i] = (float)(values[i] / norm);
        }

        return new ContentVector(null, normalized);
    }

    /// <summary>
    /// Cosine similarity of two vectors of the same kind, in [0, 1]. Mixed-kind or empty pairs
    /// (which never occur within one index) score 0.
    /// </summary>
    /// <param name="a">The first vector.</param>
    /// <param name="b">The second vector.</param>
    /// <returns>The cosine similarity in [0, 1].</returns>
    public static double Cosine(ContentVector a, ContentVector b)
    {
        if (a._dense is { } da && b._dense is { } db)
        {
            if (da.Length == 0 || da.Length != db.Length)
            {
                return 0.0;
            }

            var dot = 0.0;
            for (var i = 0; i < da.Length; i++)
            {
                dot += (double)da[i] * db[i];
            }

            return Math.Clamp(dot, 0.0, 1.0);
        }

        if (a._sparse is { } sa && b._sparse is { } sb)
        {
            return TfIdfModel.Cosine(sa, sb);
        }

        return 0.0;
    }
}
