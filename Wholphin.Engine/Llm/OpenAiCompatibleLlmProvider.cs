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

namespace Wholphin.Engine.Llm;

/// <summary>
/// <see cref="ILlmProvider"/> over any OpenAI-compatible chat-completions endpoint. Raw HTTP via
/// <see cref="IHttpClientFactory"/> — no SDK to bundle, consistent with the Jellyseerr client and the
/// plugin's single-DLL deploy. Defaults to Groq; an explicit base URL points it at OpenAI, Ollama,
/// OpenRouter, LiteLLM, etc. Reads endpoint + key + model from the admin plugin configuration, falling
/// back to the legacy Groq fields so existing installs keep working untouched. Every call is wrapped
/// so a failure (or no config) degrades to <c>null</c> instead of throwing.
/// </summary>
public class OpenAiCompatibleLlmProvider : ILlmProvider
{
    /// <summary>The endpoint used when no base URL is configured (the historical Groq default).</summary>
    public const string DefaultBaseUrl = "https://api.groq.com/openai/v1";

    private const string DefaultModel = "llama-3.3-70b-versatile";
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(25);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IEngineMetrics _metrics;
    private readonly ILogger<OpenAiCompatibleLlmProvider> _logger;

    /// <summary>Initializes a new instance of the <see cref="OpenAiCompatibleLlmProvider"/> class.</summary>
    /// <param name="httpClientFactory">The HTTP client factory provided by Jellyfin.</param>
    /// <param name="metrics">Operational metrics.</param>
    /// <param name="logger">The logger.</param>
    public OpenAiCompatibleLlmProvider(IHttpClientFactory httpClientFactory, IEngineMetrics metrics, ILogger<OpenAiCompatibleLlmProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _metrics = metrics;
        _logger = logger;
    }

    /// <inheritdoc />
    /// <remarks>An explicit base URL with no key is valid (keyless endpoints like local Ollama).</remarks>
    public bool IsConfigured
    {
        get
        {
            var config = Plugin.Instance?.Configuration;
            return !string.IsNullOrWhiteSpace(ResolveApiKey(config?.LlmApiKey, config?.GroqApiKey))
                || !string.IsNullOrWhiteSpace(config?.LlmBaseUrl);
        }
    }

    /// <inheritdoc />
    public Task<string?> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
        => CompleteAsync(
            new List<LlmMessage> { new("system", systemPrompt), new("user", userPrompt) },
            options: null,
            ct);

    /// <inheritdoc />
    public async Task<string?> CompleteAsync(IReadOnlyList<LlmMessage> messages, LlmRequestOptions? options = null, CancellationToken ct = default)
    {
        var config = Plugin.Instance?.Configuration;
        var apiKey = ResolveApiKey(config?.LlmApiKey, config?.GroqApiKey);
        var baseUrl = config?.LlmBaseUrl;
        if (string.IsNullOrWhiteSpace(apiKey) && string.IsNullOrWhiteSpace(baseUrl))
        {
            return null;
        }

        var model = ResolveModel(config?.LlmModel, config?.GroqModel);
        var opts = options ?? new LlmRequestOptions();

        try
        {
            var body = new ChatRequest
            {
                Model = model,
                Temperature = opts.Temperature,
                MaxTokens = opts.MaxTokens,
                ResponseFormat = opts.JsonMode ? new ChatResponseFormat { Type = "json_object" } : null,
            };
            foreach (var message in messages)
            {
                body.Messages.Add(new ChatMessage { Role = message.Role, Content = message.Content });
            }

            using var client = _httpClientFactory.CreateClient();
            client.Timeout = CallTimeout;

            HttpResponseMessage response;
            var attempt = 0;
            while (true)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, BuildEndpoint(baseUrl));
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
                _metrics.Increment("llm.call.ratelimited");
                _logger.LogWarning("Orca Engine: LLM chat rate-limited (429); retrying in {DelaySeconds:0}s.", delay.TotalSeconds);
                response.Dispose();
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    _metrics.Increment("llm.call.error");
                    _logger.LogWarning("Orca Engine: LLM chat returned {Status}.", (int)response.StatusCode);
                    return null;
                }

                var completion = await response.Content
                    .ReadFromJsonAsync<ChatResponse>(JsonOptions, ct)
                    .ConfigureAwait(false);
                var content = completion?.Choices is { Count: > 0 } choices ? choices[0].Message?.Content : null;
                if (string.IsNullOrWhiteSpace(content))
                {
                    _metrics.Increment("llm.call.error");
                    return null;
                }

                _metrics.Increment("llm.call.ok");
                return content;
            }
        }
        catch (Exception ex)
        {
            _metrics.Increment("llm.call.error");
            _logger.LogWarning(ex, "Orca Engine: LLM chat call failed.");
            return null;
        }
    }

    /// <summary>The chat-completions URL for a configured base URL (empty → the Groq default).</summary>
    /// <param name="baseUrl">The configured OpenAI-compatible base URL (e.g. http://localhost:11434/v1).</param>
    /// <returns>The full chat-completions endpoint.</returns>
    internal static string BuildEndpoint(string? baseUrl)
        => (string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl.Trim().TrimEnd('/')) + "/chat/completions";

    /// <summary>The effective API key: the generic field first, the legacy Groq field as fallback.</summary>
    /// <param name="llmApiKey">The generic LLM API key.</param>
    /// <param name="groqApiKey">The legacy Groq API key.</param>
    /// <returns>The trimmed key, or null when neither is set.</returns>
    internal static string? ResolveApiKey(string? llmApiKey, string? groqApiKey)
        => !string.IsNullOrWhiteSpace(llmApiKey) ? llmApiKey.Trim()
            : !string.IsNullOrWhiteSpace(groqApiKey) ? groqApiKey.Trim()
            : null;

    /// <summary>The effective model id: generic field → legacy Groq field → the built-in default.</summary>
    /// <param name="llmModel">The generic LLM model id.</param>
    /// <param name="groqModel">The legacy Groq model id.</param>
    /// <returns>The trimmed model id.</returns>
    internal static string ResolveModel(string? llmModel, string? groqModel)
        => !string.IsNullOrWhiteSpace(llmModel) ? llmModel.Trim()
            : !string.IsNullOrWhiteSpace(groqModel) ? groqModel.Trim()
            : DefaultModel;

    // --- Wire DTOs (OpenAI-compatible chat completions) ---
    private sealed class ChatRequest
    {
        [JsonPropertyName("model")] public string Model { get; set; } = string.Empty;

        [JsonPropertyName("messages")] public List<ChatMessage> Messages { get; set; } = new();

        [JsonPropertyName("temperature")] public double Temperature { get; set; }

        [JsonPropertyName("max_tokens")] public int MaxTokens { get; set; }

        [JsonPropertyName("response_format")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ChatResponseFormat? ResponseFormat { get; set; }
    }

    private sealed class ChatResponseFormat
    {
        [JsonPropertyName("type")] public string Type { get; set; } = "json_object";
    }

    private sealed class ChatMessage
    {
        [JsonPropertyName("role")] public string Role { get; set; } = string.Empty;

        [JsonPropertyName("content")] public string Content { get; set; } = string.Empty;
    }

    private sealed class ChatResponse
    {
        [JsonPropertyName("choices")] public List<ChatChoice>? Choices { get; set; }
    }

    private sealed class ChatChoice
    {
        [JsonPropertyName("message")] public ChatMessage? Message { get; set; }
    }
}
