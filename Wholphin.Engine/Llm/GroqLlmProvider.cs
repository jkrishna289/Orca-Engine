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

namespace Wholphin.Engine.Llm;

/// <summary>
/// <see cref="ILlmProvider"/> over Groq's OpenAI-compatible chat-completions API. Raw HTTP via
/// <see cref="IHttpClientFactory"/> — no SDK to bundle, consistent with the Jellyseerr client and the
/// plugin's single-DLL deploy. Reads the API key + model from the admin plugin configuration. Every
/// call is wrapped so a failure (or no key) degrades to <c>null</c> instead of throwing.
/// </summary>
public class GroqLlmProvider : ILlmProvider
{
    private const string ChatCompletionsUrl = "https://api.groq.com/openai/v1/chat/completions";
    private const string DefaultModel = "llama-3.3-70b-versatile";
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(25);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IEngineMetrics _metrics;
    private readonly ILogger<GroqLlmProvider> _logger;

    /// <summary>Initializes a new instance of the <see cref="GroqLlmProvider"/> class.</summary>
    /// <param name="httpClientFactory">The HTTP client factory provided by Jellyfin.</param>
    /// <param name="metrics">Operational metrics.</param>
    /// <param name="logger">The logger.</param>
    public GroqLlmProvider(IHttpClientFactory httpClientFactory, IEngineMetrics metrics, ILogger<GroqLlmProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _metrics = metrics;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Plugin.Instance?.Configuration?.GroqApiKey);

    /// <inheritdoc />
    public async Task<string?> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
    {
        var config = Plugin.Instance?.Configuration;
        var apiKey = config?.GroqApiKey?.Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return null;
        }

        var model = string.IsNullOrWhiteSpace(config?.GroqModel) ? DefaultModel : config!.GroqModel.Trim();

        try
        {
            var body = new GroqChatRequest
            {
                Model = model,
                Temperature = 0.2,
                MaxTokens = 1024,
                ResponseFormat = new GroqResponseFormat { Type = "json_object" },
                Messages = new List<GroqMessage>
                {
                    new() { Role = "system", Content = systemPrompt },
                    new() { Role = "user", Content = userPrompt },
                },
            };

            using var client = _httpClientFactory.CreateClient();
            client.Timeout = CallTimeout;
            using var request = new HttpRequestMessage(HttpMethod.Post, ChatCompletionsUrl);
            request.Headers.Add("Authorization", $"Bearer {apiKey}");
            request.Content = JsonContent.Create(body, options: JsonOptions);

            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _metrics.Increment("groq.call.error");
                _logger.LogWarning("Orca Engine: Groq chat returned {Status}.", (int)response.StatusCode);
                return null;
            }

            var completion = await response.Content
                .ReadFromJsonAsync<GroqChatResponse>(JsonOptions, ct)
                .ConfigureAwait(false);
            var content = completion?.Choices is { Count: > 0 } choices ? choices[0].Message?.Content : null;
            if (string.IsNullOrWhiteSpace(content))
            {
                _metrics.Increment("groq.call.error");
                return null;
            }

            _metrics.Increment("groq.call.ok");
            return content;
        }
        catch (Exception ex)
        {
            _metrics.Increment("groq.call.error");
            _logger.LogWarning(ex, "Orca Engine: Groq chat call failed.");
            return null;
        }
    }

    // --- Wire DTOs (OpenAI-compatible chat completions) ---
    private sealed class GroqChatRequest
    {
        [JsonPropertyName("model")] public string Model { get; set; } = string.Empty;

        [JsonPropertyName("messages")] public List<GroqMessage> Messages { get; set; } = new();

        [JsonPropertyName("temperature")] public double Temperature { get; set; }

        [JsonPropertyName("max_tokens")] public int MaxTokens { get; set; }

        [JsonPropertyName("response_format")] public GroqResponseFormat? ResponseFormat { get; set; }
    }

    private sealed class GroqResponseFormat
    {
        [JsonPropertyName("type")] public string Type { get; set; } = "json_object";
    }

    private sealed class GroqMessage
    {
        [JsonPropertyName("role")] public string Role { get; set; } = string.Empty;

        [JsonPropertyName("content")] public string Content { get; set; } = string.Empty;
    }

    private sealed class GroqChatResponse
    {
        [JsonPropertyName("choices")] public List<GroqChoice>? Choices { get; set; }
    }

    private sealed class GroqChoice
    {
        [JsonPropertyName("message")] public GroqMessage? Message { get; set; }
    }
}
