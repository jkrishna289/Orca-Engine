using System;
using System.Net.Http;
using Microsoft.Extensions.Logging;
using Wholphin.Engine.Diagnostics;

namespace Wholphin.Engine.Embedding;

/// <summary>
/// Embeddings from a local (or LAN) Ollama server, over its OpenAI-compatible
/// <c>/v1/embeddings</c> endpoint. The default provider: a real neural model, no API key, no quota,
/// and the text never leaves the network.
/// </summary>
/// <remarks>
/// <para>
/// It speaks the OpenAI shape exactly, so this is the shared base with three things swapped: the
/// endpoint comes from configuration, readiness is "a base URL is set" rather than "a key is set",
/// and there is no Authorization header to send.
/// </para>
/// <para>
/// The batch is smaller than a hosted provider's on purpose. Ollama processes a request's inputs
/// serially on one machine, so a large batch is not a faster round trip — it is one long request
/// that runs into the HTTP timeout. Bounded batches are what make a local model viable at all.
/// </para>
/// <para>
/// The model must already be pulled on that server (<c>ollama pull nomic-embed-text</c>). Ollama
/// answers 404 for a model it does not have, which surfaces as a failed provider rather than as
/// anything more specific.
/// </para>
/// </remarks>
public sealed class OllamaEmbeddingProvider : OpenAiCompatibleEmbeddingProvider
{
    /// <summary>The provider name used in admin config to select this provider.</summary>
    public const string ProviderName = "ollama";

    /// <summary>Initializes a new instance of the <see cref="OllamaEmbeddingProvider"/> class.</summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="metrics">Operational metrics.</param>
    /// <param name="logger">The logger.</param>
    public OllamaEmbeddingProvider(IHttpClientFactory httpClientFactory, IEngineMetrics metrics, ILogger<OllamaEmbeddingProvider> logger)
        : base(httpClientFactory, metrics, logger)
    {
    }

    /// <inheritdoc />
    public override string Name => ProviderName;

    /// <inheritdoc />
    /// <remarks>A self-hosted server on the same box has nothing to authenticate; the URL is the setting that matters.</remarks>
    public override bool IsConfigured => BaseUrl is not null;

    /// <inheritdoc />
    public override int MaxBatchSize => 32;

    /// <inheritdoc />
    protected override string EndpointUrl => $"{BaseUrl}/v1/embeddings";

    /// <inheritdoc />
    protected override string? ApiKey => null;

    /// <inheritdoc />
    protected override string Model
    {
        get
        {
            var configured = Plugin.Instance?.Configuration?.OllamaEmbeddingModel?.Trim();
            return string.IsNullOrWhiteSpace(configured) ? "nomic-embed-text" : configured;
        }
    }

    /// <summary>The configured server root with any trailing slash removed, or null when unset.</summary>
    private static string? BaseUrl
    {
        get
        {
            var configured = Plugin.Instance?.Configuration?.OllamaBaseUrl?.Trim();
            if (string.IsNullOrWhiteSpace(configured))
            {
                return null;
            }

            // "http://host:11434/" and "http://host:11434" must produce the same endpoint; a double
            // slash is the kind of thing that 404s with no useful message.
            return configured.TrimEnd('/');
        }
    }
}
