using System;
using System.Collections.Generic;

namespace Wholphin.Engine.Embedding;

/// <summary>
/// A fixed-length neural embedding, stored L2-normalized so cosine similarity is just the dot
/// product. Every <see cref="IEmbeddingProvider"/> produces these, so the recommendation layer treats
/// a local Ollama model and a hosted API identically.
/// </summary>
/// <remarks>
/// Dense only. This once also carried sparse TF-IDF vectors, and the two kinds scored 0 against each
/// other — which made "an index must never mix providers" a rule the type could not enforce. One
/// representation makes that class of bug unrepresentable; the remaining requirement is simply that
/// vectors compared together came from the same model, since dimensions differ between models.
/// </remarks>
public sealed class ContentVector
{
    /// <summary>An empty vector (cosine 0 against anything).</summary>
    public static readonly ContentVector Empty = new(null);

    private readonly float[]? _values;

    private ContentVector(float[]? values) => _values = values;

    /// <summary>Gets a value indicating whether this vector carries no signal.</summary>
    public bool IsEmpty => _values is null || _values.Length == 0;

    /// <summary>Gets the embedding values (L2-normalized), or null for an empty vector.</summary>
    public IReadOnlyList<float>? DenseValues => _values;

    /// <summary>Wraps an embedding, L2-normalizing it so cosine reduces to a dot product.</summary>
    /// <param name="values">The raw embedding values.</param>
    /// <returns>The content vector (empty when the input is empty or all-zero).</returns>
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

        return new ContentVector(normalized);
    }

    /// <summary>
    /// Combines vectors into their L2-normalized weighted mean — the way a user's taste vector is
    /// derived from their seed items' content vectors. Empty vectors, non-positive weights, and
    /// vectors whose length disagrees with the first usable one are skipped.
    /// </summary>
    /// <param name="parts">The vectors with their weights.</param>
    /// <returns>The weighted mean, or <see cref="Empty"/> when nothing usable was supplied.</returns>
    public static ContentVector WeightedMean(IReadOnlyList<(ContentVector Vector, double Weight)> parts)
    {
        double[]? sum = null;

        foreach (var (vector, weight) in parts)
        {
            if (weight <= 0 || vector._values is not { Length: > 0 } values)
            {
                continue;
            }

            sum ??= new double[values.Length];

            // A length mismatch means two different models' output reached one profile — skip it
            // rather than averaging coordinates that do not describe the same axes.
            if (values.Length != sum.Length)
            {
                continue;
            }

            for (var i = 0; i < values.Length; i++)
            {
                sum[i] += values[i] * weight;
            }
        }

        if (sum is not { Length: > 0 })
        {
            return Empty;
        }

        var mean = new float[sum.Length];
        for (var i = 0; i < sum.Length; i++)
        {
            mean[i] = (float)sum[i];
        }

        return Dense(mean);
    }

    /// <summary>
    /// Cosine similarity of two vectors, in [0, 1]. Empty or differently-sized pairs score 0.
    /// </summary>
    /// <param name="a">The first vector.</param>
    /// <param name="b">The second vector.</param>
    /// <returns>The cosine similarity in [0, 1].</returns>
    public static double Cosine(ContentVector a, ContentVector b)
    {
        if (a._values is not { Length: > 0 } va || b._values is not { Length: > 0 } vb || va.Length != vb.Length)
        {
            return 0.0;
        }

        var dot = 0.0;
        for (var i = 0; i < va.Length; i++)
        {
            dot += (double)va[i] * vb[i];
        }

        return Math.Clamp(dot, 0.0, 1.0);
    }
}
