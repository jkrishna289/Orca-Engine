using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Wholphin.Engine.Embedding;

/// <summary>
/// Default <see cref="IEmbeddingService"/>. Picks the provider named by
/// <c>PluginConfiguration.EmbeddingProvider</c> (when present and configured), otherwise the local
/// TF-IDF default; if the chosen provider returns null at call time, falls back to TF-IDF so the
/// content-similarity layer keeps working.
/// </summary>
public class EmbeddingService : IEmbeddingService
{
    private readonly IReadOnlyDictionary<string, IEmbeddingProvider> _providers;
    private readonly IEmbeddingProvider _default;
    private readonly ILogger<EmbeddingService> _logger;

    /// <summary>Initializes a new instance of the <see cref="EmbeddingService"/> class.</summary>
    /// <param name="providers">All registered embedding providers.</param>
    /// <param name="logger">The logger.</param>
    public EmbeddingService(IEnumerable<IEmbeddingProvider> providers, ILogger<EmbeddingService> logger)
    {
        var list = providers.ToList();
        _providers = list.ToDictionary(p => p.Name, p => p, StringComparer.OrdinalIgnoreCase);
        _default = _providers.TryGetValue(TfIdfEmbeddingProvider.ProviderName, out var tfidf) ? tfidf : list[0];
        _logger = logger;
    }

    /// <inheritdoc />
    public string ActiveProviderName => Resolve().Name;

    /// <inheritdoc />
    public async Task<IReadOnlyList<ContentVector>?> EmbedAsync(IReadOnlyList<string> documents, CancellationToken ct = default)
    {
        var provider = Resolve();
        var result = await provider.EmbedAsync(documents, ct).ConfigureAwait(false);
        if (result is not null)
        {
            return result;
        }

        if (!ReferenceEquals(provider, _default))
        {
            _logger.LogWarning(
                "Orca Engine: embedding provider '{Provider}' returned no vectors; falling back to TF-IDF.",
                provider.Name);
            return await _default.EmbedAsync(documents, ct).ConfigureAwait(false);
        }

        return result;
    }

    // The configured provider when it exists and is configured; else the local TF-IDF default.
    private IEmbeddingProvider Resolve()
    {
        var configured = Plugin.Instance?.Configuration?.EmbeddingProvider?.Trim();
        if (!string.IsNullOrWhiteSpace(configured)
            && _providers.TryGetValue(configured, out var provider)
            && provider.IsConfigured)
        {
            return provider;
        }

        return _default;
    }
}
