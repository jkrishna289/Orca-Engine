using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Wholphin.Engine.Llm;
using Wholphin.Engine.Personalization;
using Wholphin.Engine.Recommendation;

namespace Wholphin.Engine.Controllers;

/// <summary>
/// Personalized recommendations (dev/verification endpoint; ranking transparency included).
/// </summary>
[ApiController]
[Route("OrcaEngine/Recommendations")]
[Produces("application/json")]
public class RecommendationsController : ControllerBase
{
    private readonly IRecommender _recommender;
    private readonly ILlmReRanker _reRanker;
    private readonly ILlmProvider _llmProvider;
    private readonly IPersonalizationService _personalization;

    /// <summary>
    /// Initializes a new instance of the <see cref="RecommendationsController"/> class.
    /// </summary>
    /// <param name="recommender">The recommender.</param>
    /// <param name="reRanker">The opt-in LLM re-ranker.</param>
    /// <param name="llmProvider">The LLM provider (for configuration status).</param>
    /// <param name="personalization">The personalization service (for affinity confidence).</param>
    public RecommendationsController(
        IRecommender recommender,
        ILlmReRanker reRanker,
        ILlmProvider llmProvider,
        IPersonalizationService personalization)
    {
        _recommender = recommender;
        _reRanker = reRanker;
        _llmProvider = llmProvider;
        _personalization = personalization;
    }

    /// <summary>Returns ranked recommendations for a user.</summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <param name="limit">Maximum results (1-200; default 20).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The ranked recommendations with score breakdown.</returns>
    [HttpGet]
    [AllowAnonymous]
    public async Task<ActionResult<IReadOnlyList<RecommendationDto>>> Get(
        [FromQuery] Guid userId,
        [FromQuery] int limit,
        CancellationToken cancellationToken)
    {
        if (userId == Guid.Empty)
        {
            return BadRequest(new { error = "userId is required." });
        }

        var results = await _recommender.RecommendAsync(userId, limit <= 0 ? 20 : limit, cancellationToken).ConfigureAwait(false);
        return results.Select(RecommendationDto.From).ToList();
    }

    /// <summary>
    /// Runs the local recommender then the opt-in Groq LLM re-ranker, returning the re-ranked order
    /// with the generated row title + per-item "why" — for verifying the Stage-3 layer in isolation.
    /// </summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <param name="limit">Maximum candidates to re-rank (1-200; default 20).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The re-rank diagnostic payload.</returns>
    [HttpGet("Rerank")]
    [AllowAnonymous]
    public async Task<ActionResult<object>> Rerank(
        [FromQuery] Guid userId,
        [FromQuery] int limit,
        CancellationToken cancellationToken)
    {
        if (userId == Guid.Empty)
        {
            return BadRequest(new { error = "userId is required." });
        }

        var recs = await _recommender.RecommendAsync(userId, limit <= 0 ? 20 : limit, cancellationToken).ConfigureAwait(false);
        var items = recs.Select(r => r.Item).ToList();
        var confidence = (await _personalization.GetAsync(userId, cancellationToken).ConfigureAwait(false)).Confidence;
        var result = await _reRanker.ReRankAsync(userId, items, confidence, cancellationToken).ConfigureAwait(false);

        return new
        {
            Configured = _llmProvider.IsConfigured,
            Enabled = Plugin.Instance?.Configuration?.FeatureLlmRerank ?? false,
            Confidence = confidence,
            Applied = result.Applied,
            RowTitle = result.RowTitle,
            Items = result.Items.Select(i => new
            {
                i.Id,
                i.Title,
                Why = result.Reasons.TryGetValue(i.Id, out var why) ? why : null,
            }).ToList(),
        };
    }

    /// <summary>Recommendation projection with score breakdown.</summary>
    public class RecommendationDto
    {
        /// <summary>Gets or sets the catalog item id.</summary>
        public long CatalogItemId { get; set; }

        /// <summary>Gets or sets the Jellyfin item id (if available locally).</summary>
        public Guid? JellyfinItemId { get; set; }

        /// <summary>Gets or sets the title.</summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>Gets or sets the final blended score.</summary>
        public double Score { get; set; }

        /// <summary>Gets or sets the personalization component.</summary>
        public double Personalization { get; set; }

        /// <summary>Gets or sets the quality component.</summary>
        public double Quality { get; set; }

        /// <summary>Gets or sets the recency component.</summary>
        public double Recency { get; set; }

        /// <summary>Gets or sets the availability component.</summary>
        public double Availability { get; set; }

        /// <summary>Maps a result to its DTO.</summary>
        /// <param name="r">The result.</param>
        /// <returns>The DTO.</returns>
        public static RecommendationDto From(RecommendationResult r) => new()
        {
            CatalogItemId = r.Item.Id,
            JellyfinItemId = r.Item.JellyfinItemId,
            Title = r.Item.Title,
            Score = Math.Round(r.Score, 4),
            Personalization = r.Personalization,
            Quality = r.Quality,
            Recency = r.Recency,
            Availability = r.Availability
        };
    }
}
