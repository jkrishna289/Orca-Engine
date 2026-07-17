using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wholphin.Engine.Caching;
using Wholphin.Engine.Data.Enums;
using Wholphin.Engine.Diagnostics;
using Wholphin.Engine.Discovery;

namespace Wholphin.Engine.Llm;

/// <summary>
/// <see cref="ILlmCandidateGenerator"/> over <see cref="ILlmProvider"/>. Ports SuggestArr's
/// recommendation ladder: build the (deduped, capped) history payload → one chat call at generation
/// temperature → parse via <see cref="LlmJsonParser"/> → on a bad reply, retry once with the raw
/// reply + a corrective system message (JSON mode off, for endpoints that reject response_format) →
/// post-validate against the history. Parsed batches are cached against the profile generation so
/// manual pulls and the 2-hour cycle reuse one LLM call until taste actually changes.
/// </summary>
public class LlmCandidateGenerator : ILlmCandidateGenerator
{
    /// <summary>Seed weight at or above which a title reads as "loved" (thumbs-up/favorite territory per SignalWeights).</summary>
    public const double LovedWeightThreshold = 8.0;

    /// <summary>The rationale display cap (mirrors the re-ranker's reason cap).</summary>
    public const int MaxRationaleLength = 140;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(12);

    private readonly ILlmProvider _provider;
    private readonly ICache _cache;
    private readonly IEngineMetrics _metrics;
    private readonly ILogger<LlmCandidateGenerator> _logger;

    /// <summary>Initializes a new instance of the <see cref="LlmCandidateGenerator"/> class.</summary>
    /// <param name="provider">The LLM provider.</param>
    /// <param name="cache">The tier-1 cache.</param>
    /// <param name="metrics">Operational metrics.</param>
    /// <param name="logger">The logger.</param>
    public LlmCandidateGenerator(ILlmProvider provider, ICache cache, IEngineMetrics metrics, ILogger<LlmCandidateGenerator> logger)
    {
        _provider = provider;
        _cache = cache;
        _metrics = metrics;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LlmRecommendation>> GenerateAsync(DiscoveryContext context, MediaType mediaType, int maxResults, CancellationToken ct = default)
    {
        if (context.Profile is not { } profile || !_provider.IsConfigured || maxResults <= 0)
        {
            _metrics.Increment("llm.discovery.skip");
            return Array.Empty<LlmRecommendation>();
        }

        var cacheKey = $"llm:discovery:{context.UserId:N}:{mediaType}:{profile.GeneratedAt.Ticks}:{maxResults}";
        if (_cache.TryGet<List<LlmRecommendation>>(cacheKey, out var cached) && cached is not null)
        {
            return cached;
        }

        var history = BuildHistory(context, mediaType);
        if (history.Loved.Count + history.Watched.Count == 0)
        {
            _metrics.Increment("llm.discovery.skip");
            return Array.Empty<LlmRecommendation>();
        }

        var userPrompt = LlmDiscoveryPromptBuilder.Build(
            mediaType,
            maxResults,
            history.Loved,
            history.Watched,
            context.DislikedTitles,
            profile.TopGenres,
            profile.AvoidGenres,
            TopPeople(context));

        try
        {
            var messages = new List<LlmMessage>
            {
                new("system", LlmDiscoveryPromptBuilder.SystemPrompt),
                new("user", userPrompt),
            };

            var raw = await _provider.CompleteAsync(
                messages,
                new LlmRequestOptions { Temperature = 0.7, MaxTokens = 1500, JsonMode = true },
                ct).ConfigureAwait(false);
            var parsed = LlmJsonParser.TryParseRecommendations(raw);

            if (parsed is null)
            {
                // Corrective retry, SuggestArr-style: show the model its own reply, restate the
                // contract, and drop json_object mode (some endpoints reject response_format).
                messages.Add(new LlmMessage("assistant", raw ?? string.Empty));
                messages.Add(new LlmMessage("system", LlmDiscoveryPromptBuilder.CorrectiveSystemPrompt));
                raw = await _provider.CompleteAsync(
                    messages,
                    new LlmRequestOptions { Temperature = 0.7, MaxTokens = 1500, JsonMode = false },
                    ct).ConfigureAwait(false);
                parsed = LlmJsonParser.TryParseRecommendations(raw);
            }

            if (parsed is null)
            {
                _metrics.Increment("llm.discovery.error");
                _logger.LogWarning("Orca Engine: LLM discovery reply unusable for {MediaType} after retry.", mediaType);
                return Array.Empty<LlmRecommendation>();
            }

            var validated = Validate(parsed, history.NormalizedTitles, maxResults);
            _cache.Set(cacheKey, validated, CacheTtl);
            _metrics.Increment("llm.discovery.ok");
            return validated;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _metrics.Increment("llm.discovery.error");
            _logger.LogWarning(ex, "Orca Engine: LLM discovery generation failed for {MediaType}.", mediaType);
            return Array.Empty<LlmRecommendation>();
        }
    }

    /// <summary>
    /// Post-validation ported from SuggestArr: drop picks that are already in the history (exact
    /// normalized match, or substring containment for titles of 5+ chars to dodge false positives
    /// on short titles), blank out source titles the history doesn't contain, clamp rationales.
    /// </summary>
    internal static List<LlmRecommendation> Validate(
        IReadOnlyList<LlmRecommendation> parsed,
        IReadOnlySet<string> normalizedHistory,
        int maxResults)
    {
        var results = new List<LlmRecommendation>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rec in parsed)
        {
            var normalized = LlmDiscoveryPromptBuilder.NormalizeTitle(rec.Title);
            if (normalized.Length == 0 || !seen.Add(normalized) || IsInHistory(normalized, normalizedHistory))
            {
                continue;
            }

            var source = rec.SourceTitle is { } s && s.Length > 0
                ? LlmDiscoveryPromptBuilder.NormalizeTitle(s)
                : null;
            if (source is not null && !normalizedHistory.Contains(source))
            {
                source = null;
            }

            var rationale = rec.Rationale.Length > MaxRationaleLength
                ? rec.Rationale[..MaxRationaleLength].TrimEnd() + "…"
                : rec.Rationale;

            results.Add(rec with { Rationale = rationale, SourceTitle = source });
            if (results.Count >= maxResults)
            {
                break;
            }
        }

        return results;
    }

    private static bool IsInHistory(string normalizedTitle, IReadOnlySet<string> normalizedHistory)
    {
        if (normalizedHistory.Contains(normalizedTitle))
        {
            return true;
        }

        if (normalizedTitle.Length < 5)
        {
            return false;
        }

        foreach (var watched in normalizedHistory)
        {
            if (watched.Length >= 5
                && (watched.Contains(normalizedTitle, StringComparison.OrdinalIgnoreCase)
                    || normalizedTitle.Contains(watched, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Builds the history payload for one media type: profile seeds ranked by weight × recency
    /// decay (the PullSeeds rule), split loved/watched at <see cref="LovedWeightThreshold"/>,
    /// deduped by normalized title, capped at the tuning's history cap. Years come from the seed
    /// catalog rows on the context.
    /// </summary>
    private static (List<HistoryLine> Loved, List<HistoryLine> Watched, IReadOnlySet<string> NormalizedTitles) BuildHistory(
        DiscoveryContext context,
        MediaType mediaType)
    {
        var yearsByItemId = context.SeedItems
            .Where(i => i.ProductionYear is not null)
            .ToDictionary(i => i.Id, i => i.ProductionYear!.Value);

        var loved = new List<HistoryLine>();
        var watched = new List<HistoryLine>();
        var normalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var now = context.Now;

        var ranked = context.Profile!.Seeds
            .Where(s => s.MediaType == mediaType && !string.IsNullOrWhiteSpace(s.Title))
            .OrderByDescending(s => s.Weight * Math.Pow(0.5, Math.Max(0, (now - s.LastEventAt).TotalDays) / 90.0));

        foreach (var seed in ranked)
        {
            if (loved.Count + watched.Count >= context.Tuning.LlmHistoryCap)
            {
                break;
            }

            var title = LlmDiscoveryPromptBuilder.NormalizeTitle(seed.Title);
            if (title.Length == 0 || !normalized.Add(title))
            {
                continue;
            }

            var line = new HistoryLine(title, yearsByItemId.TryGetValue(seed.CatalogItemId, out var year) ? year : null);
            (seed.Weight >= LovedWeightThreshold ? loved : watched).Add(line);
        }

        // Dislikes count toward history matching too — the model must not re-propose them.
        foreach (var disliked in context.DislikedTitles)
        {
            var title = LlmDiscoveryPromptBuilder.NormalizeTitle(disliked.Title);
            if (title.Length > 0)
            {
                normalized.Add(title);
            }
        }

        return (loved, watched, normalized);
    }

    private static List<string> TopPeople(DiscoveryContext context)
    {
        if (context.Affinity?.Person is not { Count: > 0 } people)
        {
            return new List<string>();
        }

        // Person keys are role-prefixed ("Director:Christopher Nolan"); show just the name.
        return people
            .Where(kv => kv.Value > 0)
            .OrderByDescending(kv => kv.Value)
            .Take(4)
            .Select(kv =>
            {
                var idx = kv.Key.IndexOf(':', StringComparison.Ordinal);
                return (idx >= 0 && idx < kv.Key.Length - 1 ? kv.Key[(idx + 1)..] : kv.Key).Trim();
            })
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
