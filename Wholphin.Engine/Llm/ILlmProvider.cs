using System.Threading;
using System.Threading.Tasks;

namespace Wholphin.Engine.Llm;

/// <summary>
/// A minimal, provider-agnostic port for a single hosted-LLM chat completion. The engine uses an LLM
/// ONLY as an opt-in, additive Stage-3 helper (re-ranking the top recommendations + writing row
/// titles/"why" blurbs); all core recommendation work happens locally without it. Implementations
/// MUST fail soft — return <c>null</c> when unconfigured or on any error — so the engine degrades to
/// its local ranking rather than breaking the home. A config switch (today: Groq) lives behind this.
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
}
