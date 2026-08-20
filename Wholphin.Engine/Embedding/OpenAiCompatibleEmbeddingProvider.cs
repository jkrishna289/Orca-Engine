using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wholphin.Engine.Diagnostics;
using Wholphin.Engine.Http;

namespace Wholphin.Engine.Embedding;

/// <summary>
/// Base <see cref="IEmbeddingProvider"/> for services that speak OpenAI's <c>/v1/embeddings</c> shape
/// (request <c>{model, input:[...]}</c>, response <c>{data:[{index, embedding:[...]}]}</c>) — OpenAI,
/// Jina, Voyage, and a local Ollama server. Subclasses supply the endpoint, key, model, and any extra
/// body fields. Raw HTTP via <see cref="IHttpClientFactory"/>; fails soft to <c>null</c>.
/// </summary>
/// <remarks>
/// The API key is optional. A self-hosted endpoint on the same machine has nothing to authenticate,
/// so readiness is <see cref="IsConfigured"/>'s question to answer — a subclass whose readiness is
/// "a base URL is set" overrides it, and the Authorization header is simply omitted when there is no
/// key to send.
/// </remarks>
public abstract class OpenAiCompatibleEmbeddingProvider : IEmbeddingProvider
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IEngineMetrics _metrics;
    private readonly ILogger _logger;

    /// <summary>Initializes a new instance of the <see cref="OpenAiCompatibleEmbeddingProvider"/> class.</summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="metrics">Operational metrics.</param>
    /// <param name="logger">The logger.</param>
    protected OpenAiCompatibleEmbeddingProvider(IHttpClientFactory httpClientFactory, IEngineMetrics metrics, ILogger logger)
    {
        _httpClientFactory = httpClientFactory;
        _metrics = metrics;
        _logger = logger;
    }

    /// <inheritdoc />
    public abstract string Name { get; }

    /// <inheritdoc />
    public virtual bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);

    /// <inheritdoc />
    /// <remarks>
    /// 96 inputs per request sits inside every OpenAI-shaped provider's documented limit. Kept
    /// virtual so a provider with a tighter ceiling can lower it without touching the caller. The
    /// loop below still honours it even when the caller has already batched, because a provider owns
    /// its own wire limits regardless of who is calling.
    /// </remarks>
    public virtual int MaxBatchSize => 96;

    /// <summary>Gets the full embeddings endpoint URL.</summary>
    protected abstract string EndpointUrl { get; }

    /// <summary>Gets the API key (Bearer), or null when the endpoint needs no authentication.</summary>
    protected abstract string? ApiKey { get; }

    /// <inheritdoc />
    public string ModelId => Model;

    /// <summary>Gets the embedding model id.</summary>
    protected abstract string Model { get; }

    /// <summary>Adds any provider-specific request body fields (e.g. input_type).</summary>
    /// <param name="body">The request body to augment.</param>
    protected virtual void Augment(Dictionary<string, object?> body)
    {
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ContentVector>?> EmbedAsync(IReadOnlyList<string> documents, CancellationToken ct = default)
    {
        if (!IsConfigured || documents.Count == 0)
        {
            return null;
        }

        var apiKey = ApiKey?.Trim();

        var all = new List<ContentVector>(documents.Count);
        try
        {
            var batchSize = Math.Max(1, MaxBatchSize);
            for (var start = 0; start < documents.Count; start += batchSize)
            {
                var count = Math.Min(batchSize, documents.Count - start);
                var chunk = new List<string>(count);
                for (var i = 0; i < count; i++)
                {
                    chunk.Add(documents[start + i]);
                }

                var chunkVectors = await EmbedChunkAsync(apiKey, chunk, ct).ConfigureAwait(false);
                if (chunkVectors is null)
                {
                    return null;
                }

                all.AddRange(chunkVectors);
            }

            _metrics.Increment($"embedding.{Name}.ok");
            return all;
        }
        catch (Exception ex)
        {
            _metrics.Increment($"embedding.{Name}.error");
            _logger.LogWarning(ex, "Orca Engine: {Provider} embedding call failed.", Name);
            return null;
        }
    }

    private async Task<IReadOnlyList<ContentVector>?> EmbedChunkAsync(string? apiKey, List<string> chunk, CancellationToken ct)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = Model,
            ["input"] = chunk,
        };
        Augment(body);

        using var client = _httpClientFactory.CreateClient(OrcaMetricsHandler.ClientName);
        client.Timeout = CallTimeout;

        HttpResponseMessage response;
        var attempt = 0;
        while (true)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, EndpointUrl);
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                request.Headers.Add("Authorization", $"Bearer {apiKey}");
            }

            request.Content = JsonContent.Create(body, options: JsonOptions);

            response = await client.SendAsync(request, ct).ConfigureAwait(false);
            attempt++;

            if (response.StatusCode != HttpStatusCode.TooManyRequests || attempt >= RateLimitRetry.MaxAttempts)
            {
                break;
            }

            var delay = RateLimitRetry.DelayFor(response);
            _metrics.Increment($"embedding.{Name}.ratelimited");
            _logger.LogWarning("Orca Engine: {Provider} embeddings rate-limited (429); retrying in {DelaySeconds:0}s.", Name, delay.TotalSeconds);
            response.Dispose();
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                _metrics.Increment($"embedding.{Name}.error");
                _logger.LogWarning("Orca Engine: {Provider} embeddings returned {Status}.", Name, (int)response.StatusCode);
                return null;
            }

            var parsed = await response.Content.ReadFromJsonAsync<EmbeddingResponse>(JsonOptions, ct).ConfigureAwait(false);
            if (parsed?.Data is not { } data || data.Count != chunk.Count)
            {
                return null;
            }

            var ordered = new ContentVector[chunk.Count];
            foreach (var datum in data)
            {
                if (datum.Index < 0 || datum.Index >= ordered.Length || datum.Embedding is not { Length: > 0 })
                {
                    return null;
                }

                ordered[datum.Index] = ContentVector.Dense(datum.Embedding);
            }

            return ordered;
        }
    }

    private sealed class EmbeddingResponse
    {
        [JsonPropertyName("data")] public List<EmbeddingDatum>? Data { get; set; }
    }

    private sealed class EmbeddingDatum
    {
        [JsonPropertyName("index")] public int Index { get; set; }

        [JsonPropertyName("embedding")] public float[]? Embedding { get; set; }
    }
}

/// <summary>OpenAI text embeddings (<c>text-embedding-3-*</c>).</summary>
public sealed class OpenAiEmbeddingProvider : OpenAiCompatibleEmbeddingProvider
{
    /// <summary>Initializes a new instance of the <see cref="OpenAiEmbeddingProvider"/> class.</summary>
    public OpenAiEmbeddingProvider(IHttpClientFactory httpClientFactory, IEngineMetrics metrics, ILogger<OpenAiEmbeddingProvider> logger)
        : base(httpClientFactory, metrics, logger)
    {
    }

    /// <inheritdoc />
    public override string Name => "openai";

    /// <inheritdoc />
    protected override string EndpointUrl => "https://api.openai.com/v1/embeddings";

    /// <inheritdoc />
    protected override string? ApiKey => Plugin.Instance?.Configuration?.OpenAiApiKey;

    /// <inheritdoc />
    protected override string Model =>
        Coalesce(Plugin.Instance?.Configuration?.OpenAiEmbeddingModel, "text-embedding-3-small");

    private static string Coalesce(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}

/// <summary>Jina embeddings (OpenAI-compatible endpoint).</summary>
public sealed class JinaEmbeddingProvider : OpenAiCompatibleEmbeddingProvider
{
    /// <summary>Initializes a new instance of the <see cref="JinaEmbeddingProvider"/> class.</summary>
    public JinaEmbeddingProvider(IHttpClientFactory httpClientFactory, IEngineMetrics metrics, ILogger<JinaEmbeddingProvider> logger)
        : base(httpClientFactory, metrics, logger)
    {
    }

    /// <inheritdoc />
    public override string Name => "jina";

    /// <inheritdoc />
    protected override string EndpointUrl => "https://api.jina.ai/v1/embeddings";

    /// <inheritdoc />
    protected override string? ApiKey => Plugin.Instance?.Configuration?.JinaApiKey;

    /// <inheritdoc />
    protected override string Model =>
        string.IsNullOrWhiteSpace(Plugin.Instance?.Configuration?.JinaEmbeddingModel)
            ? "jina-embeddings-v3"
            : Plugin.Instance!.Configuration!.JinaEmbeddingModel.Trim();

    /// <inheritdoc />
    protected override void Augment(Dictionary<string, object?> body) => body["embedding_type"] = "float";
}

/// <summary>Voyage AI embeddings (OpenAI-style response).</summary>
public sealed class VoyageEmbeddingProvider : OpenAiCompatibleEmbeddingProvider
{
    /// <summary>Initializes a new instance of the <see cref="VoyageEmbeddingProvider"/> class.</summary>
    public VoyageEmbeddingProvider(IHttpClientFactory httpClientFactory, IEngineMetrics metrics, ILogger<VoyageEmbeddingProvider> logger)
        : base(httpClientFactory, metrics, logger)
    {
    }

    /// <inheritdoc />
    public override string Name => "voyage";

    /// <inheritdoc />
    protected override string EndpointUrl => "https://api.voyageai.com/v1/embeddings";

    /// <inheritdoc />
    protected override string? ApiKey => Plugin.Instance?.Configuration?.VoyageApiKey;

    /// <inheritdoc />
    protected override string Model =>
        string.IsNullOrWhiteSpace(Plugin.Instance?.Configuration?.VoyageEmbeddingModel)
            ? "voyage-3.5"
            : Plugin.Instance!.Configuration!.VoyageEmbeddingModel.Trim();

    /// <inheritdoc />
    protected override void Augment(Dictionary<string, object?> body) => body["input_type"] = "document";
}
