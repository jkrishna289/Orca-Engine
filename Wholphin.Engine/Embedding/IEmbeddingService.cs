using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Wholphin.Engine.Embedding;

/// <summary>
/// Resolves the admin-selected <see cref="IEmbeddingProvider"/> and embeds documents through it,
/// transparently falling back to local TF-IDF when the chosen provider is unconfigured or fails — so
/// callers always get usable vectors regardless of the cloud setup.
/// </summary>
public interface IEmbeddingService
{
    /// <summary>Gets the name of the provider that will be used for embedding (post-fallback resolution).</summary>
    string ActiveProviderName { get; }

    /// <summary>Embeds the documents via the active provider, falling back to TF-IDF on failure.</summary>
    /// <param name="documents">The catalog text documents.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>One vector per document (TF-IDF vectors at worst; never null in practice).</returns>
    Task<IReadOnlyList<ContentVector>?> EmbedAsync(IReadOnlyList<string> documents, CancellationToken ct = default);
}
