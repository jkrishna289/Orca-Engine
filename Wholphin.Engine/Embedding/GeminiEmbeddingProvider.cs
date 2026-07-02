using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wholphin.Engine.Diagnostics;

namespace Wholphin.Engine.Embedding;

/// <summary>
/// <see cref="IEmbeddingProvider"/> over Google Gemini's <c>batchEmbedContents</c> API. Distinct from
/// the OpenAI shape: the key rides the <c>x-goog-api-key</c> header, the body is a list of
/// <c>requests</c> with <c>content.parts[].text</c>, and the reply is <c>embeddings[].values</c>.
/// Raw HTTP, fails soft to <c>null</c>.
/// </summary>
public class GeminiEmbeddingProvider : IEmbeddingProvider
{
    private const string DefaultModel = "gemini-embedding-001";
    private const int BatchSize = 96;
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IEngineMetrics _metrics;
    private readonly ILogger<GeminiEmbeddingProvider> _logger;

    /// <summary>Initializes a new instance of the <see cref="GeminiEmbeddingProvider"/> class.</summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="metrics">Operational metrics.</param>
    /// <param name="logger">The logger.</param>
    public GeminiEmbeddingProvider(IHttpClientFactory httpClientFactory, IEngineMetrics metrics, ILogger<GeminiEmbeddingProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _metrics = metrics;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "gemini";

    /// <inheritdoc />
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Plugin.Instance?.Configuration?.GeminiApiKey);

    /// <inheritdoc />
    public async Task<IReadOnlyList<ContentVector>?> EmbedAsync(IReadOnlyList<string> documents, CancellationToken ct = default)
    {
        var apiKey = Plugin.Instance?.Configuration?.GeminiApiKey?.Trim();
        if (string.IsNullOrWhiteSpace(apiKey) || documents.Count == 0)
        {
            return null;
        }

        var model = string.IsNullOrWhiteSpace(Plugin.Instance?.Configuration?.GeminiEmbeddingModel)
            ? DefaultModel
            : Plugin.Instance!.Configuration!.GeminiEmbeddingModel.Trim();
        var modelRef = $"models/{model}";
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:batchEmbedContents";

        var all = new List<ContentVector>(documents.Count);
        try
        {
            for (var start = 0; start < documents.Count; start += BatchSize)
            {
                var count = Math.Min(BatchSize, documents.Count - start);
                var requests = new List<GeminiEmbedRequest>(count);
                for (var i = 0; i < count; i++)
                {
                    requests.Add(new GeminiEmbedRequest
                    {
                        Model = modelRef,
                        Content = new GeminiContent
                        {
                            Parts = new List<GeminiPart> { new() { Text = documents[start + i] } },
                        },
                    });
                }

                var chunk = await EmbedChunkAsync(url, apiKey, requests, ct).ConfigureAwait(false);
                if (chunk is null)
                {
                    return null;
                }

                all.AddRange(chunk);
            }

            _metrics.Increment("embedding.gemini.ok");
            return all;
        }
        catch (Exception ex)
        {
            _metrics.Increment("embedding.gemini.error");
            _logger.LogWarning(ex, "Orca Engine: Gemini embedding call failed.");
            return null;
        }
    }

    private async Task<IReadOnlyList<ContentVector>?> EmbedChunkAsync(string url, string apiKey, List<GeminiEmbedRequest> requests, CancellationToken ct)
    {
        using var client = _httpClientFactory.CreateClient();
        client.Timeout = CallTimeout;
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("x-goog-api-key", apiKey);
        request.Content = JsonContent.Create(new GeminiBatchRequest { Requests = requests }, options: JsonOptions);

        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _metrics.Increment("embedding.gemini.error");
            _logger.LogWarning("Orca Engine: Gemini embeddings returned {Status}.", (int)response.StatusCode);
            return null;
        }

        var parsed = await response.Content.ReadFromJsonAsync<GeminiBatchResponse>(JsonOptions, ct).ConfigureAwait(false);
        if (parsed?.Embeddings is not { } embeddings || embeddings.Count != requests.Count)
        {
            return null;
        }

        var vectors = new List<ContentVector>(embeddings.Count);
        foreach (var embedding in embeddings)
        {
            if (embedding.Values is not { Length: > 0 } values)
            {
                return null;
            }

            vectors.Add(ContentVector.Dense(values));
        }

        return vectors;
    }

    private sealed class GeminiBatchRequest
    {
        [JsonPropertyName("requests")] public List<GeminiEmbedRequest> Requests { get; set; } = new();
    }

    private sealed class GeminiEmbedRequest
    {
        [JsonPropertyName("model")] public string Model { get; set; } = string.Empty;

        [JsonPropertyName("content")] public GeminiContent Content { get; set; } = new();
    }

    private sealed class GeminiContent
    {
        [JsonPropertyName("parts")] public List<GeminiPart> Parts { get; set; } = new();
    }

    private sealed class GeminiPart
    {
        [JsonPropertyName("text")] public string Text { get; set; } = string.Empty;
    }

    private sealed class GeminiBatchResponse
    {
        [JsonPropertyName("embeddings")] public List<GeminiEmbeddingValues>? Embeddings { get; set; }
    }

    private sealed class GeminiEmbeddingValues
    {
        [JsonPropertyName("values")] public float[]? Values { get; set; }
    }
}
