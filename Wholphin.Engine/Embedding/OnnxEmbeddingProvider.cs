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
/// returns <c>null</c> until inference is implemented here. Keeping the port in place means enabling
/// it later is purely additive.
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
    /// <remarks>
    /// Small by design: on-box inference is bounded by RAM and a single CPU/GPU, so a large batch
    /// buys nothing and risks an allocation spike on the machine already serving playback.
    /// </remarks>
    public int MaxBatchSize => 32;

    /// <inheritdoc />
    public string ModelId => Plugin.Instance?.Configuration?.OnnxModelPath?.Trim() ?? string.Empty;

    /// <inheritdoc />
    public Task<IReadOnlyList<ContentVector>?> EmbedAsync(IReadOnlyList<string> documents, CancellationToken ct = default)
    {
        // Placeholder: on-box ONNX inference is not implemented in this build.
        _logger.LogInformation(
            "Orca Engine: ONNX embedding provider is selected but not implemented in this build.");
        return Task.FromResult<IReadOnlyList<ContentVector>?>(null);
    }
}
