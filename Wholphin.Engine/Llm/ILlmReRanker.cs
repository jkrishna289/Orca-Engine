using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wholphin.Engine.Data.Entities;

namespace Wholphin.Engine.Llm;

/// <summary>
/// The outcome of an LLM re-rank: a (possibly reordered) item list, an optional generated row title,
/// and short per-item "why" blurbs keyed by catalog item id.
/// </summary>
public sealed class LlmReRankResult
{
    /// <summary>Gets the items in their final order (the input order when not applied).</summary>
    public IReadOnlyList<CatalogItem> Items { get; init; } = Array.Empty<CatalogItem>();

    /// <summary>Gets the LLM-generated row title, or null to keep the default.</summary>
    public string? RowTitle { get; init; }

    /// <summary>Gets the per-item "why recommended" blurbs, keyed by catalog item id.</summary>
    public IReadOnlyDictionary<long, string> Reasons { get; init; } = new Dictionary<long, string>();

    /// <summary>Gets a value indicating whether the LLM actually re-ranked (vs. a fail-soft passthrough).</summary>
    public bool Applied { get; init; }

    /// <summary>Builds a passthrough result that preserves the input order with no title/reasons.</summary>
    /// <param name="items">The items to pass through unchanged.</param>
    /// <returns>A non-applied result.</returns>
    public static LlmReRankResult PassThrough(IReadOnlyList<CatalogItem> items) => new() { Items = items };
}

/// <summary>
/// One LLM curation job: pick the best <see cref="Count"/> titles from <see cref="Pool"/> for a
/// stated purpose. The local engine always produces the pool — the LLM only selects/orders within
/// it (index-referenced, so it can never invent titles). Used by every personalized surface when
/// <c>FeatureLlmCuration</c> is on; global list rows (trending/country/recent) never curate.
/// </summary>
public sealed class LlmCurationRequest
{
    /// <summary>Gets the surface id ("foryou", "mood", "similar", "upcoming") — metric + cache namespace.</summary>
    public required string Purpose { get; init; }

    /// <summary>Gets the candidate pool the LLM may select from (local-engine prefiltered).</summary>
    public required IReadOnlyList<CatalogItem> Pool { get; init; }

    /// <summary>Gets how many picks the surface wants.</summary>
    public required int Count { get; init; }

    /// <summary>Gets the user whose anonymized taste summary flavors the pick (null = taste-free curation).</summary>
    public Guid? UserId { get; init; }

    /// <summary>Gets the user's affinity confidence (taste-based purposes gate cold profiles out).</summary>
    public double Confidence { get; init; } = 1.0;

    /// <summary>Gets the seed title for seed-anchored purposes (More Like This), or null.</summary>
    public CatalogItem? Seed { get; init; }

    /// <summary>Gets an extra purpose line for the prompt (e.g. the mood theme, "airing in the next 3 weeks").</summary>
    public string? Context { get; init; }

    /// <summary>Gets a value indicating whether the LLM may generate a row title.</summary>
    public bool WantTitle { get; init; }

    /// <summary>
    /// Gets a value indicating whether dropped candidates stay dropped (true = curation: the row IS
    /// the LLM's picks) or are re-appended after the picks (false = reorder-only: nothing is lost,
    /// the LLM only decides prominence).
    /// </summary>
    public bool Selection { get; init; } = true;

    /// <summary>Gets the cache lifetime for this curation (null = the default 6 hours).</summary>
    public TimeSpan? CacheTtl { get; init; }
}

/// <summary>
/// Opt-in Stage-3 LLM re-ranker. Given the local recommender's top candidates, asks a hosted LLM
/// (Groq) to reorder them for the viewer and generate a row title + "why" blurbs — purely additive
/// over the local ranking. Self-gating (a no-op unless configured + enabled + the profile is warm)
/// and fail-soft (returns the input order on any miss), so the home never depends on it. Only an
/// anonymized taste summary + public catalog metadata are sent off-box — never user identity.
/// </summary>
public interface ILlmReRanker
{
    /// <summary>
    /// General LLM curation over a local-engine candidate pool (For You, moods, similar, upcoming).
    /// Gated by <c>FeatureLlmCuration</c>; fail-soft passthrough of the pool order on any miss.
    /// </summary>
    /// <param name="request">The curation job.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The curated result (passthrough on any miss).</returns>
    Task<LlmReRankResult> CurateAsync(LlmCurationRequest request, CancellationToken ct = default);

    /// <summary>Re-ranks the candidates with the LLM, or returns them unchanged when not applicable.</summary>
    /// <param name="userId">The Jellyfin user id (used to fetch the anonymized taste profile + cache).</param>
    /// <param name="candidates">The local recommender's ranked candidates.</param>
    /// <param name="confidence">The user's affinity confidence (gates cold profiles out).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The re-rank result (passthrough on any miss).</returns>
    Task<LlmReRankResult> ReRankAsync(Guid userId, IReadOnlyList<CatalogItem> candidates, double confidence, CancellationToken ct = default);

    /// <summary>
    /// Genuine LLM "Because You Watched": given the title the viewer just watched and a candidate
    /// pool (content-similarity prefilter), asks the LLM which candidates a fan of the seed would
    /// genuinely watch next — with a short "why" per pick. No confidence gate: the seed itself is
    /// the context, so this works from the very first watch. Passthrough (input order) on any miss.
    /// </summary>
    /// <param name="seed">The most recently watched title seeding the row.</param>
    /// <param name="candidates">The candidate pool to pick from (similarity-prefiltered).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The picked/ordered result (passthrough on any miss).</returns>
    Task<LlmReRankResult> PickForSeedAsync(CatalogItem seed, IReadOnlyList<CatalogItem> candidates, CancellationToken ct = default);
}
