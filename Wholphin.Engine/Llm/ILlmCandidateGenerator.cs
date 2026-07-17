using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wholphin.Engine.Data.Enums;
using Wholphin.Engine.Discovery;

namespace Wholphin.Engine.Llm;

/// <summary>
/// Generates external discovery candidates by asking the configured LLM to propose titles from the
/// viewer's watch history (the SuggestArr pattern: one batched call per media type, holistic taste
/// analysis). Fail-soft: returns an empty list when unconfigured, when parsing is exhausted, or on
/// any error, so the TMDB sources fill in.
/// </summary>
public interface ILlmCandidateGenerator
{
    /// <summary>Asks the LLM for recommendations for one media type, with the corrective-retry ladder.</summary>
    /// <param name="context">The discovery run context (profile, seeds, dislikes, tuning).</param>
    /// <param name="mediaType">Movie or Series.</param>
    /// <param name="maxResults">How many recommendations to ask for.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The validated, pre-TMDB-resolution recommendations (empty on any miss).</returns>
    Task<IReadOnlyList<LlmRecommendation>> GenerateAsync(DiscoveryContext context, MediaType mediaType, int maxResults, CancellationToken ct = default);
}
