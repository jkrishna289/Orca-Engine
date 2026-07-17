using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Wholphin.Engine.Llm;

/// <summary>One chat message in an LLM conversation ("system" | "user" | "assistant").</summary>
/// <param name="Role">The OpenAI-compatible role.</param>
/// <param name="Content">The message text.</param>
public sealed record LlmMessage(string Role, string Content);

/// <summary>Per-call completion knobs. The defaults reproduce the historical re-ranker behavior.</summary>
public sealed class LlmRequestOptions
{
    /// <summary>Gets the sampling temperature.</summary>
    public double Temperature { get; init; } = 0.2;

    /// <summary>Gets the completion token ceiling.</summary>
    public int MaxTokens { get; init; } = 1024;

    /// <summary>
    /// Gets a value indicating whether to request <c>response_format: json_object</c>. Disable for
    /// endpoints that reject the parameter (some Ollama/proxy builds) and rely on prompting instead.
    /// </summary>
    public bool JsonMode { get; init; } = true;
}

/// <summary>
/// A minimal, provider-agnostic port for a single hosted-LLM chat completion. The engine uses an LLM
/// ONLY as an opt-in, additive helper (re-ranking the top recommendations, generating discovery
/// candidates, writing row titles/"why" blurbs); all core recommendation work happens locally without
/// it. Implementations MUST fail soft — return <c>null</c> when unconfigured or on any error — so the
/// engine degrades to its local ranking rather than breaking the home. Any OpenAI-compatible endpoint
/// can live behind this (Groq by default).
/// </summary>
public interface ILlmProvider
{
    /// <summary>Gets a value indicating whether the provider has the configuration it needs to run.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Sends a system + user prompt and returns the model's reply text (expected to be JSON), or
    /// <c>null</c> when unconfigured or on any failure.
    /// </summary>
    /// <param name="systemPrompt">The system instruction.</param>
    /// <param name="userPrompt">The user content.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The model's reply text, or null on miss.</returns>
    Task<string?> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct = default);

    /// <summary>
    /// Sends a full message list with per-call options and returns the model's reply text, or
    /// <c>null</c> when unconfigured or on any failure. Used by callers that need retry
    /// conversations (corrective re-prompts) or non-default sampling.
    /// </summary>
    /// <param name="messages">The ordered chat messages.</param>
    /// <param name="options">Per-call knobs; null uses the defaults.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The model's reply text, or null on miss.</returns>
    Task<string?> CompleteAsync(IReadOnlyList<LlmMessage> messages, LlmRequestOptions? options = null, CancellationToken ct = default);
}
