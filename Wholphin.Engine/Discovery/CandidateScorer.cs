using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wholphin.Engine.Embedding;

namespace Wholphin.Engine.Discovery;

/// <summary>
/// Pipeline stage 3 — pure ranking signals only. Computes the <see cref="DiscoveryScore"/>
/// component breakdown per candidate: taste (same-batch embedding cosine against the user's
/// seed-mean vector + a small genre-affinity bonus), popularity, freshness, novelty, the weak
/// country boost, memory-driven interest/exposure, and source confidence. Never drops a
/// candidate and never writes — thresholds belong to selection, persistence to the write stage.
/// </summary>
public class CandidateScorer
{
    /// <summary>Selection taste-floor for sparse (TF-IDF) vectors (small-batch cosines run low).</summary>
    public const double SparseTasteFloor = 0.12;

    /// <summary>Selection taste-floor for dense (neural) vectors (cosines run much higher).</summary>
    public const double DenseTasteFloor = 0.30;

    private const double CountryBoostMax = 0.05;
    private const double GenreAffinityBonus = 0.10;
    private const double FreshnessRampYears = 10.0;

    private readonly IEmbeddingService _embeddings;
    private readonly Ranking.IScoringPolicy _scoring;
    private readonly ILogger<CandidateScorer> _logger;

    /// <summary>Initializes a new instance of the <see cref="CandidateScorer"/> class.</summary>
    /// <param name="embeddings">The embedding service (one batch per run).</param>
    /// <param name="scoring">The shared scoring policy (blend weights).</param>
    /// <param name="logger">The logger.</param>
    public CandidateScorer(IEmbeddingService embeddings, Ranking.IScoringPolicy scoring, ILogger<CandidateScorer> logger)
    {
        _embeddings = embeddings;
        _scoring = scoring;
        _logger = logger;
    }

    /// <summary>The outcome of a scoring pass.</summary>
    /// <param name="Scored">Every candidate with its score breakdown (nothing dropped).</param>
    /// <param name="TasteFloor">The selection threshold matching the vector kind used for taste.</param>
    public sealed record ScoringResult(List<ScoredCandidate> Scored, double TasteFloor);

    /// <summary>Scores a filtered candidate batch.</summary>
    /// <param name="context">The run context.</param>
    /// <param name="candidates">The eligible candidates.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The scored candidates plus the applicable taste floor.</returns>
    public async Task<ScoringResult> ScoreAsync(DiscoveryContext context, IReadOnlyList<DiscoveryCandidate> candidates, CancellationToken ct = default)
    {
        var tasteFloor = SparseTasteFloor;
        var cosines = new double[candidates.Count];

        // Taste needs the user vector and every candidate embedded IN ONE BATCH: TF-IDF fits its
        // model per call, so vectors from different calls live in different spaces. Seeds are
        // embedded alongside the candidates and the user vector is their weighted mean within
        // this very batch — internally consistent by construction.
        if (context.Profile is { Seeds.Count: > 0 } profile && context.SeedItems.Count > 0 && candidates.Count > 0)
        {
            try
            {
                var documents = new List<string>(context.SeedItems.Count + candidates.Count);
                documents.AddRange(context.SeedItems.Select(ContentDocument.Of));
                documents.AddRange(candidates.Select(c => ContentDocument.Of(c.Result)));

                var vectors = await _embeddings.EmbedAsync(documents, ct).ConfigureAwait(false);
                if (vectors is { } all && all.Count == documents.Count)
                {
                    var weightById = profile.Seeds.ToDictionary(s => s.CatalogItemId, s => s.Weight);
                    var seedParts = context.SeedItems
                        .Select((item, i) => (all[i], weightById.GetValueOrDefault(item.Id)))
                        .ToList();
                    var userVector = ContentVector.WeightedMean(seedParts);
                    if (!userVector.IsEmpty)
                    {
                        tasteFloor = userVector.DenseValues is not null ? DenseTasteFloor : SparseTasteFloor;
                        for (var i = 0; i < candidates.Count; i++)
                        {
                            cosines[i] = ContentVector.Cosine(userVector, all[context.SeedItems.Count + i]);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Taste degrades to the genre-affinity bonus alone; the run still completes.
                _logger.LogWarning(ex, "Orca Engine: discovery embedding batch failed; scoring without cosine taste.");
            }
        }

        var maxPopularity = candidates.Count > 0 ? candidates.Max(c => c.Result.Popularity ?? 0.0) : 0.0;
        var userLanguage = CountryLanguages.For(context.Settings.CountryCode);
        var scored = new List<ScoredCandidate>(candidates.Count);
        for (var i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            var result = candidate.Result;
            context.Memory.TryGetValue((result.TmdbId, result.MediaType), out var memory);

            var score = new DiscoveryScore
            {
                Taste = Math.Clamp(cosines[i] + (GenreAffinityBonus * TasteAffinity(context, result)), 0, 1),
                Popularity = maxPopularity > 0 ? Math.Clamp((result.Popularity ?? 0.0) / maxPopularity, 0, 1) : 0,
                Freshness = Freshness(context.Now, result.Year),
                Novelty = Novelty(context, result),
                CountryBoost = context.UserId != Guid.Empty
                    && userLanguage is not null
                    && string.Equals(result.OriginalLanguage, userLanguage, StringComparison.OrdinalIgnoreCase)
                        ? CountryBoostMax
                        : 0,
                InterestMultiplier = InterestModel.EffectiveInterest(memory, context.Now),
                ExposurePenalty = InterestModel.ExposurePenalty(memory),
                SourceConfidence = candidate.Attributions.Count > 0 ? candidate.Attributions.Max(a => a.SourceConfidence) : 0,
            };

            score.Final = ComputeFinal(score, _scoring.Discovery);
            scored.Add(new ScoredCandidate(candidate, score));
        }

        return new ScoringResult(scored, tasteFloor);
    }

    /// <summary>
    /// Recomputes a score's final value from the shared scoring vocabulary (used again after the
    /// diversity penalty lands). Pure — the weights are supplied so it stays deterministic + testable.
    /// </summary>
    /// <param name="score">The discovery score breakdown.</param>
    /// <param name="weights">The discovery blend weights.</param>
    /// <returns>The blended final score, rounded.</returns>
    public static double ComputeFinal(DiscoveryScore score, Ranking.ScoringWeights weights)
    {
        var signals = new Ranking.RecommendationSignals
        {
            Taste = score.Taste,
            Popularity = score.Popularity,
            Freshness = score.Freshness,
            Novelty = score.Novelty,
            SourceConfidence = score.SourceConfidence,
            CountryBoost = score.CountryBoost,
            InterestMultiplier = score.InterestMultiplier,
            ExposurePenalty = score.ExposurePenalty,
            DiversityPenalty = score.DiversityPenalty,
        };
        return Math.Round(signals.Blend(weights), 4);
    }

    private static double Freshness(DateTime now, int? year)
        => year is { } y ? Math.Clamp(1.0 - (Math.Max(0, now.Year - y) / FreshnessRampYears), 0, 1) : 0;

    /// <summary>
    /// A structured-affinity bonus for a candidate, 0..1: the mean positive genre affinity, lifted
    /// by the user's affinity for the candidate's original language when known. Stabilizes taste
    /// scoring when overviews (and thus cosine) are thin.
    /// </summary>
    private static double TasteAffinity(DiscoveryContext context, Integrations.Jellyseerr.DiscoverResult result)
    {
        if (context.Affinity is not { } affinity)
        {
            return 0;
        }

        var genreMean = 0.0;
        if (result.Genres.Count > 0)
        {
            var sum = 0.0;
            foreach (var genre in result.Genres)
            {
                sum += Math.Max(0, affinity.Genre.GetValueOrDefault(genre));
            }

            genreMean = sum / result.Genres.Count;
        }

        var language = string.IsNullOrWhiteSpace(result.OriginalLanguage)
            ? 0.0
            : Math.Max(0, affinity.Language.GetValueOrDefault(result.OriginalLanguage!));

        return Math.Clamp(Math.Max(genreMean, language), 0, 1);
    }

    /// <summary>
    /// Novelty for an external candidate — quality × genre unfamiliarity, zero for loved or
    /// disliked genres. Mirrors <see cref="Recommendation.ExplorationScorer"/>, which operates on
    /// catalog items and therefore can't consume a <see cref="Integrations.Jellyseerr.DiscoverResult"/>.
    /// </summary>
    private static double Novelty(DiscoveryContext context, Integrations.Jellyseerr.DiscoverResult result)
    {
        if (context.Affinity is not { } affinity || result.Genres.Count == 0)
        {
            return 0;
        }

        var quality = Math.Clamp((result.CommunityRating ?? 0) / 10.0, 0, 1);
        if (quality <= 0)
        {
            return 0;
        }

        var maxAffinity = double.NegativeInfinity;
        foreach (var genre in result.Genres)
        {
            maxAffinity = Math.Max(maxAffinity, affinity.Genre.GetValueOrDefault(genre));
        }

        if (maxAffinity < 0)
        {
            return 0;
        }

        return quality * Math.Clamp(1 - maxAffinity, 0, 1);
    }
}
