using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Wholphin.Engine.Embedding;

/// <summary>
/// Resolves the admin-selected <see cref="IEmbeddingProvider"/> and embeds documents through it in
/// bounded batches. There is no fallback provider: two models produce vectors of different dimension
/// and geometry, so a failure is reported rather than papered over with something weaker.
/// </summary>
public interface IEmbeddingService
{
    /// <summary>Gets the name of the provider that will be used for embedding.</summary>
    string ActiveProviderName { get; }

    /// <summary>
    /// Gets the model the active provider will use. Persisted with every stored vector, because a
    /// model change invalidates them all even when the provider has not changed.
    /// </summary>
    string ActiveModelId { get; }

    /// <summary>
    /// Embeds a small, self-contained set of documents in ONE call.
    /// </summary>
    /// <param name="documents">The documents. Must be small enough for a single provider call.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>One vector per document, or null when the provider produced nothing.</returns>
    /// <remarks>
    /// For sets that must share one vector space and are known to be small — discovery scoring
    /// embeds a user's seeds alongside the candidates so their cosine is meaningful, and splitting
    /// that would defeat the point. Use <see cref="EmbedCorpusAsync"/> for anything catalog-sized.
    /// </remarks>
    Task<IReadOnlyList<ContentVector>?> EmbedAsync(IReadOnlyList<string> documents, CancellationToken ct = default);

    /// <summary>
    /// Embeds a whole corpus in bounded batches, with bounded retries and a reported outcome.
    /// </summary>
    /// <param name="documents">The corpus. Batched internally; may be catalog-sized.</param>
    /// <param name="progress">Optional per-batch progress. Never reported per document.</param>
    /// <param name="ct">Cancellation token; cancellation propagates rather than degrading the run.</param>
    /// <returns>The run outcome — vectors positionally aligned with <paramref name="documents"/>.</returns>
    /// <remarks>
    /// Check <see cref="EmbeddingRun.Succeeded"/> before using the vectors: a run that stopped part
    /// way returns none at all, so the caller keeps whatever index it already had.
    /// </remarks>
    Task<EmbeddingRun> EmbedCorpusAsync(
        IReadOnlyList<string> documents,
        IProgress<EmbeddingProgress>? progress = null,
        CancellationToken ct = default);
}
