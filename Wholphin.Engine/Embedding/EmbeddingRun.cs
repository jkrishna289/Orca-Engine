using System;
using System.Collections.Generic;

namespace Wholphin.Engine.Embedding;

/// <summary>
/// The outcome of embedding a whole corpus in bounded batches.
/// </summary>
/// <remarks>
/// A plain vector list cannot express a partial run, and a partial run is the normal failure here:
/// the corpus is batched, so a rebuild can stop anywhere. <see cref="Succeeded"/> is the only thing
/// a caller should branch on; the counts exist so an operator can see how far it got.
/// </remarks>
/// <param name="Vectors">
/// One vector per input document, positionally aligned, or empty when the run failed outright.
/// </param>
/// <param name="ProviderName">The provider that produced <paramref name="Vectors"/>.</param>
/// <param name="DocumentCount">How many documents were submitted.</param>
/// <param name="BatchSize">Documents per call.</param>
/// <param name="BatchesCompleted">Batches that returned a well-formed result.</param>
/// <param name="BatchesFailed">Batches that exhausted their retries.</param>
/// <param name="Failure">Why the run failed, when it did.</param>
public sealed record EmbeddingRun(
    IReadOnlyList<ContentVector> Vectors,
    string ProviderName,
    int DocumentCount,
    int BatchSize,
    int BatchesCompleted,
    int BatchesFailed,
    string? Failure)
{
    /// <summary>Gets a value indicating whether the run produced a complete, usable vector set.</summary>
    public bool Succeeded => Vectors.Count == DocumentCount && DocumentCount > 0;

    /// <summary>An empty corpus: nothing to do, and not a failure.</summary>
    /// <param name="providerName">The active provider.</param>
    /// <returns>An empty successful run.</returns>
    public static EmbeddingRun Empty(string providerName) =>
        new(Array.Empty<ContentVector>(), providerName, 0, 0, 0, 0, null);

    /// <summary>A run that produced nothing usable at all.</summary>
    /// <param name="providerName">The provider that was asked for.</param>
    /// <param name="documentCount">How many documents were submitted.</param>
    /// <param name="batchSize">The batch size in use.</param>
    /// <param name="batchesCompleted">Batches that had succeeded before the run was abandoned.</param>
    /// <param name="batchesFailed">Batches that exhausted their retries.</param>
    /// <param name="failure">Why it failed.</param>
    /// <returns>A failed run.</returns>
    public static EmbeddingRun Failed(
        string providerName,
        int documentCount,
        int batchSize,
        int batchesCompleted,
        int batchesFailed,
        string failure) =>
        new(
            Array.Empty<ContentVector>(),
            providerName,
            documentCount,
            batchSize,
            batchesCompleted,
            batchesFailed,
            failure);
}

/// <summary>
/// Batch-level progress for a corpus embedding run. Reported per batch, never per document — a
/// catalog of thousands would otherwise emit thousands of log lines to say one thing.
/// </summary>
/// <param name="ProviderName">The provider being called.</param>
/// <param name="DocumentCount">Total documents in the run.</param>
/// <param name="BatchSize">Documents per batch.</param>
/// <param name="BatchesCompleted">Batches finished so far.</param>
/// <param name="BatchesTotal">Batches the run will attempt.</param>
/// <param name="DocumentsEmbedded">Documents with a vector so far.</param>
public readonly record struct EmbeddingProgress(
    string ProviderName,
    int DocumentCount,
    int BatchSize,
    int BatchesCompleted,
    int BatchesTotal,
    int DocumentsEmbedded);
