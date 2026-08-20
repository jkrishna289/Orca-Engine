using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Wholphin.Engine.Embedding;

/// <summary>
/// Turns catalog text documents into <see cref="ContentVector"/>s for similarity. The default is a
/// local Ollama server (no key, no cloud); optional adapters call hosted embedding APIs (OpenAI,
/// Gemini, Voyage, Jina) or a local ONNX model. The active one is chosen by admin config and resolved
/// through <see cref="IEmbeddingService"/>. There is no fallback between providers: two models
/// produce vectors of different dimension and geometry, so one cannot stand in for another.
/// </summary>
public interface IEmbeddingProvider
{
    /// <summary>Gets the config key that selects this provider (e.g. "ollama", "openai", "gemini").</summary>
    string Name { get; }

    /// <summary>Gets a value indicating whether the provider has what it needs to run (a server URL or an API key).</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Gets the identity of the model in use (e.g. "nomic-embed-text").
    /// </summary>
    /// <remarks>
    /// Persisted alongside every stored vector. The provider name alone is not enough to decide
    /// whether a stored vector can be reused — two models behind the same provider have different
    /// dimensions and different geometry, so swapping the model must invalidate the index.
    /// </remarks>
    string ModelId { get; }

    /// <summary>
    /// Gets the largest number of documents that may be passed to <see cref="EmbedAsync"/> in one
    /// call. The batching layer reads this instead of branching on <see cref="Name"/>, so a hosted
    /// API and a local server are batched by the same code at different sizes.
    /// </summary>
    int MaxBatchSize { get; }

    /// <summary>
    /// Embeds a batch of documents.
    /// </summary>
    /// <param name="documents">The catalog text documents. Never empty — callers filter first.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>One vector per document in the same order, or <c>null</c> on failure.</returns>
    /// <remarks>
    /// <para><b>The contract callers rely on, stated explicitly:</b></para>
    /// <list type="bullet">
    /// <item><description>
    /// <b>Ordering.</b> The result is positional: index <c>i</c> is the vector for
    /// <c>documents[i]</c>. A provider whose wire format returns results out of order (OpenAI's
    /// <c>data[].index</c>) MUST reorder before returning. Callers pair results with source
    /// documents by position and have no other way to re-associate them.
    /// </description></item>
    /// <item><description>
    /// <b>Count.</b> On success the result has exactly <c>documents.Count</c> entries. A short,
    /// long, or partially-populated result is a failure and must be reported as <c>null</c>, never
    /// returned — a caller cannot tell which documents a short result skipped.
    /// </description></item>
    /// <item><description>
    /// <b>Empty input.</b> Callers do not pass empty lists. A provider handed one may return
    /// <c>null</c>; nothing depends on the distinction.
    /// </description></item>
    /// <item><description>
    /// <b>Failure.</b> Return <c>null</c> rather than throwing. <c>null</c> means "this call did not
    /// produce vectors" and is treated as potentially transient — <see cref="IEmbeddingService"/>
    /// retries it a bounded number of times before giving up on the rebuild entirely.
    /// </description></item>
    /// <item><description>
    /// <b>Cancellation.</b> <see cref="OperationCanceledException"/> is expected to propagate, and is
    /// never treated as a provider failure. A cancelled rebuild must not look like a broken provider.
    /// </description></item>
    /// <item><description>
    /// <b>Size.</b> Honour <see cref="MaxBatchSize"/> internally as well. Callers batch to it, but a
    /// provider is still responsible for staying inside its own wire limits.
    /// </description></item>
    /// <item><description>
    /// <b>Dimensions.</b> Every vector from one provider has the same length.
    /// <see cref="ContentVector.Cosine"/> scores differently-sized pairs as 0, so a snapshot must
    /// never mix output from two providers.
    /// </description></item>
    /// </list>
    /// </remarks>
    Task<IReadOnlyList<ContentVector>?> EmbedAsync(IReadOnlyList<string> documents, CancellationToken ct = default);
}
