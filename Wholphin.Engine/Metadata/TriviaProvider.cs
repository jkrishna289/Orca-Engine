using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wholphin.Engine.Caching;
using Wholphin.Engine.Data.Entities;
using Wholphin.Engine.Data.Enums;
using Wholphin.Engine.Diagnostics;
using Wholphin.Engine.Integrations.Tmdb;
using Wholphin.Engine.Llm;
using Wholphin.Engine.Personalization;

namespace Wholphin.Engine.Metadata;

/// <summary>
/// Default <see cref="ITriviaProvider"/>. Asks the configured LLM (Groq) for a few "Did You Know?"
/// facts; when the LLM is unavailable, falls back to listing the title's themes (TMDB keywords / tags).
/// Cached: long for real facts, shorter for empties so enabling the LLM later refreshes promptly.
/// </summary>
public class TriviaProvider : ITriviaProvider
{
    private const int MaxFacts = 5;
    private const int MaxFactLength = 180;
    private const int OverviewLimit = 400;

    private static readonly TimeSpan FactsTtl = TimeSpan.FromDays(30);
    private static readonly TimeSpan EmptyTtl = TimeSpan.FromHours(6);

    private const string System =
        "You are a film and TV trivia assistant. Reply ONLY with compact JSON of the form " +
        "{\"facts\":[\"fact 1\",\"fact 2\"]}. Each fact is a short, well-known, verifiable \"Did You Know?\" " +
        "tidbit (awards, true-story basis, filming locations, casting notes, box office, cultural impact). " +
        "Give 3 to 5 facts. No spoilers. If unsure, return fewer or an empty array. Never invent facts.";

    private readonly ILlmProvider _llm;
    private readonly ITmdbClient _tmdb;
    private readonly ICache _cache;
    private readonly IEngineMetrics _metrics;
    private readonly ILogger<TriviaProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TriviaProvider"/> class.
    /// </summary>
    public TriviaProvider(ILlmProvider llm, ITmdbClient tmdb, ICache cache, IEngineMetrics metrics, ILogger<TriviaProvider> logger)
    {
        _llm = llm;
        _tmdb = tmdb;
        _cache = cache;
        _metrics = metrics;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetTriviaAsync(CatalogItem item, CancellationToken ct = default)
    {
        var cacheKey = $"trivia:{item.Id}";
        if (_cache.TryGet<List<string>>(cacheKey, out var cached) && cached is not null)
        {
            return cached;
        }

        var facts = await ProduceAsync(item, ct).ConfigureAwait(false);
        _cache.Set(cacheKey, facts, facts.Count > 0 ? FactsTtl : EmptyTtl);
        return facts;
    }

    private async Task<List<string>> ProduceAsync(CatalogItem item, CancellationToken ct)
    {
        if (_llm.IsConfigured)
        {
            try
            {
                var reply = await _llm.CompleteAsync(System, BuildPrompt(item), ct).ConfigureAwait(false);
                var facts = ParseFacts(reply);
                if (facts.Count > 0)
                {
                    _metrics.Increment("trivia.llm.ok");
                    return facts;
                }

                _metrics.Increment("trivia.llm.empty");
            }
            catch (Exception ex)
            {
                _metrics.Increment("trivia.llm.error");
                _logger.LogWarning(ex, "Orca Engine: trivia LLM call failed for {Title}.", item.Title);
            }
        }

        return await FallbackAsync(item, ct).ConfigureAwait(false);
    }

    private static string BuildPrompt(CatalogItem item)
    {
        var type = item.MediaType == MediaType.Series ? "TV series" : "movie";
        var year = item.ProductionYear is { } y ? $" ({y.ToString(CultureInfo.InvariantCulture)})" : string.Empty;
        var overview = string.IsNullOrWhiteSpace(item.Overview)
            ? string.Empty
            : " Overview: " + (item.Overview!.Length > OverviewLimit ? item.Overview[..OverviewLimit] : item.Overview);
        return $"{type} title: \"{item.Title}\"{year}.{overview}";
    }

    private static List<string> ParseFacts(string? reply)
    {
        var facts = new List<string>();
        if (string.IsNullOrWhiteSpace(reply))
        {
            return facts;
        }

        try
        {
            using var doc = JsonDocument.Parse(reply);
            JsonElement arr;
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                arr = doc.RootElement;
            }
            else if (!doc.RootElement.TryGetProperty("facts", out arr) || arr.ValueKind != JsonValueKind.Array)
            {
                return facts;
            }

            foreach (var el in arr.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var fact = el.GetString()?.Trim();
                if (string.IsNullOrWhiteSpace(fact))
                {
                    continue;
                }

                if (fact.Length > MaxFactLength)
                {
                    fact = fact[..MaxFactLength].TrimEnd() + "…";
                }

                if (!facts.Contains(fact, StringComparer.OrdinalIgnoreCase))
                {
                    facts.Add(fact);
                }

                if (facts.Count >= MaxFacts)
                {
                    break;
                }
            }
        }
        catch (JsonException)
        {
            // Non-JSON reply → no facts.
        }

        return facts;
    }

    private async Task<List<string>> FallbackAsync(CatalogItem item, CancellationToken ct)
    {
        // No LLM: surface the title's themes (TMDB keywords / tags) as a single "themes" line.
        var themes = CatalogFeatures.Parse(item.TagsJson).Take(5).ToList();
        if (themes.Count == 0 && _tmdb.IsConfigured && item.TmdbId is { } tmdb)
        {
            themes = (await _tmdb.GetKeywordsAsync(tmdb, item.MediaType, ct).ConfigureAwait(false)).Take(5).ToList();
        }

        return themes.Count > 0
            ? new List<string> { "Themes: " + string.Join(", ", themes) }
            : new List<string>();
    }
}
