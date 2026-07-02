using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wholphin.Engine.Recommendation;

namespace Wholphin.Engine.Embedding;

/// <summary>
/// The default, always-available <see cref="IEmbeddingProvider"/>: local TF-IDF. Tokenizes the batch,
/// fits an inverse-document-frequency model over it, and projects each document into a sparse,
/// L2-normalized <see cref="ContentVector"/>. No API key, no network — works offline and is the
/// fallback when a cloud provider is unconfigured or fails.
/// </summary>
public class TfIdfEmbeddingProvider : IEmbeddingProvider
{
    /// <summary>The provider name used in admin config to select this provider.</summary>
    public const string ProviderName = "tfidf";

    /// <inheritdoc />
    public string Name => ProviderName;

    /// <inheritdoc />
    public bool IsConfigured => true;

    /// <inheritdoc />
    public Task<IReadOnlyList<ContentVector>?> EmbedAsync(IReadOnlyList<string> documents, CancellationToken ct = default)
    {
        var tokenized = new List<IReadOnlyList<string>>(documents.Count);
        foreach (var document in documents)
        {
            tokenized.Add(ContentTokens.Tokenize(document));
        }

        var model = TfIdfModel.Build(tokenized);

        var vectors = new List<ContentVector>(documents.Count);
        foreach (var tokens in tokenized)
        {
            vectors.Add(ContentVector.Sparse(model.Vectorize(tokens)));
        }

        return Task.FromResult<IReadOnlyList<ContentVector>?>(vectors);
    }
}
