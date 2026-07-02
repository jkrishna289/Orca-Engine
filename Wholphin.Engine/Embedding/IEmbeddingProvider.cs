using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Wholphin.Engine.Embedding;

/// <summary>
/// Turns catalog text documents into <see cref="ContentVector"/>s for similarity. The default is
/// local TF-IDF (no key, no cloud); optional adapters call hosted embedding APIs (OpenAI, Gemini,
/// Voyage, Jina) or a local ONNX model. The active one is chosen by admin config and resolved
/// through <see cref="IEmbeddingService"/>, which falls back to TF-IDF when a cloud provider is
/// unconfigured or fails — so the engine always has working vectors.
/// </summary>
public interface IEmbeddingProvider
{
    /// <summary>Gets the config key that selects this provider (e.g. "tfidf", "openai", "gemini").</summary>
    string Name { get; }

    /// <summary>Gets a value indicating whether the provider has what it needs to run (e.g. an API key).</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Embeds a batch of documents, returning one vector per document in the same order, or
    /// <c>null</c> on failure (the caller falls back). Local TF-IDF uses the batch as its corpus;
    /// neural providers embed each document.
    /// </summary>
    /// <param name="documents">The catalog text documents.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>One vector per document, or null on failure.</returns>
    Task<IReadOnlyList<ContentVector>?> EmbedAsync(IReadOnlyList<string> documents, CancellationToken ct = default);
}
