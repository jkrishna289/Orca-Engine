using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Wholphin.Engine.Embedding;

/// <summary>
/// Local, on-box neural embeddings via an ONNX sentence-transformer model — the architecture slot for
/// "fully local semantic vectors, no cloud, no key".
/// <para>
/// NOT yet wired: real inference needs the <c>Microsoft.ML.OnnxRuntime</c> package + a bundled model +
/// a WordPiece/SentencePiece tokenizer, which conflicts with the plugin's single-DLL deploy. This
/// provider therefore reports configured only when a model path is set, but <see cref="EmbedAsync"/>
/// returns <c>null</c> (the <see cref="IEmbeddingService"/> falls back to TF-IDF) until inference is
/// implemented here. Keeping the port in place means enabling it later is purely additive.
/// </para>
/// </summary>
public class OnnxEmbeddingProvider : IEmbeddingProvider
{
    private readonly ILogger<OnnxEmbeddingProvider> _logger;

    /// <summary>Initializes a new instance of the <see cref="OnnxEmbeddingProvider"/> class.</summary>
    /// <param name="logger">The logger.</param>
    public OnnxEmbeddingProvider(ILogger<OnnxEmbeddingProvider> logger) => _logger = logger;

    /// <inheritdoc />
    public string Name => "onnx";

    /// <inheritdoc />
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Plugin.Instance?.Configuration?.OnnxModelPath);

    /// <inheritdoc />
    public Task<IReadOnlyList<ContentVector>?> EmbedAsync(IReadOnlyList<string> documents, CancellationToken ct = default)
    {
        // Placeholder: on-box ONNX inference is not implemented in this build. Returning null makes the
        // service fall back to TF-IDF so selecting "onnx" degrades gracefully rather than breaking.
        _logger.LogInformation(
            "Orca Engine: ONNX embedding provider is selected but not implemented in this build; falling back to TF-IDF.");
        return Task.FromResult<IReadOnlyList<ContentVector>?>(null);
    }
}
