using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wholphin.Engine.Caching;
using Wholphin.Engine.Data.Entities;
using Wholphin.Engine.Diagnostics;
using Wholphin.Engine.Personalization;

namespace Wholphin.Engine.Llm;

/// <summary>
/// Default <see cref="ILlmReRanker"/>: builds an anonymized taste summary + a compact candidate list,
/// asks the <see cref="ILlmProvider"/> (Groq) to reorder and annotate them, parses the JSON reply,
/// and caches the result. Self-gating + fail-soft at every step.
/// </summary>
public class LlmReRanker : ILlmReRanker
{
    // Bound the prompt: send at most this many candidates; not worth a network round-trip below the min.
    private const int MaxCandidates = 20;
    private const int MinCandidates = 4;

    // Match the home's personalized-layout threshold — only re-rank warm profiles.
    private const double MinConfidence = 0.40;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);

    private const string SystemPrompt =
        "You re-rank candidate movie/TV titles for one viewer of a personal media server, using their "
        + "taste profile. Return ONLY a JSON object of this exact shape: "
        + "{\"order\":[<candidate indices, best first>],\"title\":\"<short row title describing the shared theme/genre/mood, NOT the name of any single title, max 40 chars>\","
        + "\"reasons\":{\"<index>\":\"<one short sentence on why it fits this viewer, max 100 chars>\"}}. "
        + "Rules: 'order' contains only indices from the candidates, no duplicates, best match first; you "
        + "may drop weak matches. Give 'reasons' for at least the top few. Never invent titles or facts. "
        + "Output JSON only, no prose.";

    private const string SeedSystemPrompt =
        "A viewer of a personal media server just watched a title. From the candidate titles, pick the "
        + "ones a fan of that title would GENUINELY want to watch next — shared tone, themes, creators, "
        + "or story appeal, not just matching genre labels. Return ONLY a JSON object of this exact shape: "
        + "{\"order\":[<candidate indices, best first>],"
        + "\"reasons\":{\"<index>\":\"<one short sentence tying the pick to the watched title, max 100 chars>\"}}. "
        + "Rules: 'order' contains only indices from the candidates, no duplicates, best first; DROP weak "
        + "matches rather than padding the list. Give 'reasons' for at least the top few. Never invent "
        + "titles or facts. Output JSON only, no prose.";

    // Curation (FeatureLlmCuration) sends a wider pool and lets the model genuinely drop weak fits.
    private const int CurationMaxCandidates = 60;
    private static readonly TimeSpan CurationCacheTtl = TimeSpan.FromHours(6);

    private const string CurationSystemPrompt =
        "You curate one row of a personal media server for its stated purpose. From the numbered "
        + "candidate titles, pick the best matches, best first. Return ONLY a JSON object of this exact shape: "
        + "{\"order\":[<candidate indices, best first>],\"title\":\"<short row title, only if asked, max 40 chars>\","
        + "\"reasons\":{\"<index>\":\"<one short sentence why it fits, max 100 chars>\"}}. "
        + "Rules: 'order' contains only indices from the candidates, no duplicates; DROP candidates "
        + "that fit the purpose poorly rather than padding; give a reason for every pick; never "
        + "invent titles or facts. Output JSON only, no prose.";

    private readonly ILlmProvider _provider;
    private readonly IPersonalizationService _personalization;
    private readonly ICache _cache;
    private readonly IEngineMetrics _metrics;
    private readonly ILogger<LlmReRanker> _logger;

    /// <summary>Initializes a new instance of the <see cref="LlmReRanker"/> class.</summary>
    /// <param name="provider">The LLM provider port.</param>
    /// <param name="personalization">The personalization service (anonymized taste profile).</param>
    /// <param name="cache">The L1 cache.</param>
    /// <param name="metrics">Operational metrics.</param>
    /// <param name="logger">The logger.</param>
    public LlmReRanker(
        ILlmProvider provider,
        IPersonalizationService personalization,
        ICache cache,
        IEngineMetrics metrics,
        ILogger<LlmReRanker> logger)
    {
        _provider = provider;
        _personalization = personalization;
        _cache = cache;
        _metrics = metrics;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<LlmReRankResult> CurateAsync(LlmCurationRequest request, CancellationToken ct = default)
    {
        var pool = request.Pool;
        if (pool.Count < MinCandidates
            || request.Count <= 0
            || Plugin.Instance?.Configuration?.FeatureLlmCuration != true
            || !_provider.IsConfigured
            || (request.UserId is { } && request.Confidence < MinConfidence))
        {
            return LlmReRankResult.PassThrough(pool);
        }

        if (pool.Count > CurationMaxCandidates)
        {
            pool = pool.Take(CurationMaxCandidates).ToList();
        }

        var scope = request.Seed is { } s ? $"seed{s.Id}" : request.UserId is { } u ? u.ToString("N") : "all";
        var cacheKey = $"llm:curate:{request.Purpose}:{scope}:{string.Join('-', pool.Select(p => p.Id))}:{request.Count}";
        if (_cache.TryGet<LlmReRankResult>(cacheKey, out var cached) && cached is not null)
        {
            return cached;
        }

        try
        {
            var sb = new StringBuilder();
            sb.Append("Purpose: ").AppendLine(request.Context ?? "the viewer's personalized recommendations row");
            sb.Append("Pick the best ").Append(request.Count.ToString(CultureInfo.InvariantCulture))
              .AppendLine(request.WantTitle
                  ? " candidates. Also give a short row title describing their shared theme (NOT the name of any single title)."
                  : " candidates. Do not include a title field.");
            sb.AppendLine();

            if (request.UserId is { } userId)
            {
                var affinity = await _personalization.GetAsync(userId, ct).ConfigureAwait(false);
                AppendTasteSummary(sb, affinity);
                sb.AppendLine();
            }

            if (request.Seed is { } seed)
            {
                AppendSeedBlock(sb, seed);
                sb.AppendLine();
            }

            AppendCandidates(sb, pool);

            var json = await _provider.CompleteAsync(CurationSystemPrompt, sb.ToString(), ct).ConfigureAwait(false);
            var result = string.IsNullOrWhiteSpace(json)
                ? null
                : Parse(json!, pool, pool, selection: request.Selection, count: request.Count);
            if (result is null)
            {
                _metrics.Increment($"llm.curate.{request.Purpose}.skip");
                return LlmReRankResult.PassThrough(request.Pool);
            }

            _metrics.Increment($"llm.curate.{request.Purpose}.ok");
            _cache.Set(cacheKey, result, request.CacheTtl ?? CurationCacheTtl);
            return result;
        }
        catch (Exception ex)
        {
            _metrics.Increment($"llm.curate.{request.Purpose}.error");
            _logger.LogWarning(ex, "Orca Engine: LLM curation failed for {Purpose}.", request.Purpose);
            return LlmReRankResult.PassThrough(request.Pool);
        }
    }

    /// <inheritdoc />
    public async Task<LlmReRankResult> ReRankAsync(
        Guid userId,
        IReadOnlyList<CatalogItem> candidates,
        double confidence,
        CancellationToken ct = default)
    {
        if (userId == Guid.Empty
            || candidates.Count < MinCandidates
            || confidence < MinConfidence
            || Plugin.Instance?.Configuration?.FeatureLlmRerank != true
            || !_provider.IsConfigured)
        {
            return LlmReRankResult.PassThrough(candidates);
        }

        var pool = candidates.Count > MaxCandidates
            ? candidates.Take(MaxCandidates).ToList()
            : candidates;

        var cacheKey = CacheKey(userId, pool);
        if (_cache.TryGet<LlmReRankResult>(cacheKey, out var cached) && cached is not null)
        {
            return cached;
        }

        try
        {
            var affinity = await _personalization.GetAsync(userId, ct).ConfigureAwait(false);
            var userPrompt = BuildUserPrompt(affinity, pool);

            var json = await _provider.CompleteAsync(SystemPrompt, userPrompt, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json))
            {
                _metrics.Increment("llm.rerank.skip");
                return LlmReRankResult.PassThrough(candidates);
            }

            var result = Parse(json, pool, candidates);
            if (result is null)
            {
                _metrics.Increment("llm.rerank.error");
                return LlmReRankResult.PassThrough(candidates);
            }

            _metrics.Increment("llm.rerank.ok");
            _cache.Set(cacheKey, result, CacheTtl);
            return result;
        }
        catch (Exception ex)
        {
            _metrics.Increment("llm.rerank.error");
            _logger.LogWarning(ex, "Orca Engine: LLM re-rank failed for {UserId}.", userId);
            return LlmReRankResult.PassThrough(candidates);
        }
    }

    /// <inheritdoc />
    public async Task<LlmReRankResult> PickForSeedAsync(
        CatalogItem seed,
        IReadOnlyList<CatalogItem> candidates,
        CancellationToken ct = default)
    {
        // No confidence gate: the seed IS the context, so this works from the very first watch.
        if (candidates.Count < MinCandidates
            || Plugin.Instance?.Configuration?.FeatureLlmRerank != true
            || !_provider.IsConfigured)
        {
            return LlmReRankResult.PassThrough(candidates);
        }

        var pool = candidates.Count > MaxCandidates
            ? candidates.Take(MaxCandidates).ToList()
            : candidates;

        var cacheKey = $"llm:byw:{seed.Id}:{string.Join('-', pool.Select(p => p.Id))}";
        if (_cache.TryGet<LlmReRankResult>(cacheKey, out var cached) && cached is not null)
        {
            return cached;
        }

        try
        {
            var json = await _provider.CompleteAsync(SeedSystemPrompt, BuildSeedPrompt(seed, pool), ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json))
            {
                _metrics.Increment("llm.byw.skip");
                return LlmReRankResult.PassThrough(candidates);
            }

            var result = Parse(json, pool, candidates);
            if (result is null)
            {
                _metrics.Increment("llm.byw.error");
                return LlmReRankResult.PassThrough(candidates);
            }

            _metrics.Increment("llm.byw.ok");
            _cache.Set(cacheKey, result, CacheTtl);
            return result;
        }
        catch (Exception ex)
        {
            _metrics.Increment("llm.byw.error");
            _logger.LogWarning(ex, "Orca Engine: LLM Because-You-Watched failed for seed {SeedId}.", seed.Id);
            return LlmReRankResult.PassThrough(candidates);
        }
    }

    private static string BuildSeedPrompt(CatalogItem seed, IReadOnlyList<CatalogItem> pool)
    {
        var sb = new StringBuilder();
        AppendSeedBlock(sb, seed);
        sb.AppendLine();
        AppendCandidates(sb, pool);
        return sb.ToString();
    }

    private static void AppendSeedBlock(StringBuilder sb, CatalogItem seed)
    {
        var seedGenres = string.Join(", ", CatalogFeatures.Parse(seed.GenresJson).Take(5));
        var seedYear = seed.ProductionYear?.ToString(CultureInfo.InvariantCulture) ?? "n/a";
        sb.Append("Just watched: ").Append(string.IsNullOrWhiteSpace(seed.Title) ? "(untitled)" : seed.Title)
          .Append(" (").Append(seedYear).Append(") | Type: ").Append(seed.MediaType)
          .Append(" | Genres: ").AppendLine(string.IsNullOrEmpty(seedGenres) ? "n/a" : seedGenres);
        if (!string.IsNullOrWhiteSpace(seed.Overview))
        {
            var overview = seed.Overview!;
            sb.Append("About: ").AppendLine(overview.Length > 300 ? overview[..300] : overview);
        }
    }

    private static void AppendCandidates(StringBuilder sb, IReadOnlyList<CatalogItem> pool)
    {
        sb.AppendLine("Candidates:");
        for (var i = 0; i < pool.Count; i++)
        {
            var item = pool[i];
            var genres = string.Join(", ", CatalogFeatures.Parse(item.GenresJson).Take(4));
            var year = item.ProductionYear?.ToString(CultureInfo.InvariantCulture) ?? "n/a";
            var rating = item.CommunityRating?.ToString("0.0", CultureInfo.InvariantCulture) ?? "n/a";
            var title = string.IsNullOrWhiteSpace(item.Title) ? "(untitled)" : item.Title;
            sb.Append(i.ToString(CultureInfo.InvariantCulture)).Append(") ").Append(title)
              .Append(" (").Append(year).Append(") | Type: ").Append(item.MediaType)
              .Append(" | Genres: ").Append(string.IsNullOrEmpty(genres) ? "n/a" : genres)
              .Append(" | Rating: ").Append(rating)
              .AppendLine();
        }
    }

    // Keyed by the exact candidate set+order so re-uses are exact and a changed slate re-queries.
    private static string CacheKey(Guid userId, IReadOnlyList<CatalogItem> pool)
        => $"llm:rerank:{userId:N}:{string.Join('-', pool.Select(p => p.Id))}";

    private static string BuildUserPrompt(AffinityVector affinity, IReadOnlyList<CatalogItem> pool)
    {
        var sb = new StringBuilder();
        AppendTasteSummary(sb, affinity);
        sb.AppendLine();
        AppendCandidates(sb, pool);
        return sb.ToString();
    }

    private static void AppendTasteSummary(StringBuilder sb, AffinityVector affinity)
    {
        sb.AppendLine("Viewer taste profile (anonymized):");
        AppendTopPositive(sb, "Likes genres", affinity.Genre, 6, stripRolePrefix: false);
        AppendTopNegative(sb, "Avoids genres", affinity.Genre, 4);
        AppendTopPositive(sb, "Likes people", affinity.Person, 4, stripRolePrefix: true);
        AppendTopPositive(sb, "Prefers type", affinity.MediaType, 2, stripRolePrefix: false);
    }

    private static void AppendTopPositive(StringBuilder sb, string label, Dictionary<string, double> dim, int take, bool stripRolePrefix)
    {
        var top = dim.Where(kv => kv.Value > 0)
            .OrderByDescending(kv => kv.Value)
            .Take(take)
            .Select(kv => Clean(kv.Key, stripRolePrefix))
            .Where(s => s.Length > 0)
            .ToList();
        if (top.Count > 0)
        {
            sb.Append("- ").Append(label).Append(": ").AppendLine(string.Join(", ", top));
        }
    }

    private static void AppendTopNegative(StringBuilder sb, string label, Dictionary<string, double> dim, int take)
    {
        var bottom = dim.Where(kv => kv.Value < 0)
            .OrderBy(kv => kv.Value)
            .Take(take)
            .Select(kv => Clean(kv.Key, stripRolePrefix: false))
            .Where(s => s.Length > 0)
            .ToList();
        if (bottom.Count > 0)
        {
            sb.Append("- ").Append(label).Append(": ").AppendLine(string.Join(", ", bottom));
        }
    }

    // People keys are role-prefixed ("Director:Christopher Nolan"); show just the name to the model.
    private static string Clean(string key, bool stripRolePrefix)
    {
        if (!stripRolePrefix)
        {
            return key.Trim();
        }

        var idx = key.IndexOf(':');
        return (idx >= 0 && idx < key.Length - 1 ? key[(idx + 1)..] : key).Trim();
    }

    internal static LlmReRankResult? Parse(
        string json,
        IReadOnlyList<CatalogItem> pool,
        IReadOnlyList<CatalogItem> all,
        bool selection = false,
        int? count = null)
    {
        // The reply may arrive fenced/prose-wrapped from lenient endpoints; extract defensively.
        var extracted = LlmJsonParser.ExtractJsonObject(json);
        if (extracted is null)
        {
            return null;
        }

        using var doc = JsonDocument.Parse(extracted);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("order", out var orderEl)
            || orderEl.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var ordered = new List<CatalogItem>(all.Count);
        var used = new HashSet<int>();
        foreach (var el in orderEl.EnumerateArray())
        {
            if (TryIndex(el, pool.Count, out var idx) && used.Add(idx))
            {
                ordered.Add(pool[idx]);
            }
        }

        if (ordered.Count == 0)
        {
            return null;
        }

        if (selection)
        {
            // Curation mode: the row IS the model's picks — dropped candidates stay dropped.
            if (count is { } cap && ordered.Count > cap)
            {
                ordered = ordered.Take(cap).ToList();
            }
        }
        else
        {
            // Reorder mode: re-append any pool items the model omitted (original order), then the
            // tail beyond the pool — nothing is lost, the model only decides prominence.
            for (var i = 0; i < pool.Count; i++)
            {
                if (!used.Contains(i))
                {
                    ordered.Add(pool[i]);
                }
            }

            for (var i = pool.Count; i < all.Count; i++)
            {
                ordered.Add(all[i]);
            }
        }

        string? title = null;
        if (root.TryGetProperty("title", out var titleEl) && titleEl.ValueKind == JsonValueKind.String)
        {
            var t = titleEl.GetString()?.Trim();
            if (!string.IsNullOrWhiteSpace(t))
            {
                title = t!.Length > 60 ? t[..60] : t;
            }
        }

        // Guard: the model sometimes echoes a candidate's name as the row title (e.g. "Raakh"), which
        // makes the row look mislabeled. Reject a title that matches any candidate so it falls back to
        // the generic "For You".
        if (title is not null && pool.Any(p => string.Equals(p.Title?.Trim(), title, StringComparison.OrdinalIgnoreCase)))
        {
            title = null;
        }

        var reasons = new Dictionary<long, string>();
        if (root.TryGetProperty("reasons", out var reasonsEl) && reasonsEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in reasonsEl.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.String
                    || !int.TryParse(prop.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx)
                    || idx < 0 || idx >= pool.Count)
                {
                    continue;
                }

                var why = prop.Value.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(why))
                {
                    reasons[pool[idx].Id] = why!.Length > 140 ? why[..140] : why;
                }
            }
        }

        return new LlmReRankResult { Items = ordered, RowTitle = title, Reasons = reasons, Applied = true };
    }

    private static bool TryIndex(JsonElement el, int count, out int idx)
    {
        idx = -1;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n))
        {
            idx = n;
        }
        else if (el.ValueKind == JsonValueKind.String
                 && int.TryParse(el.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s))
        {
            idx = s;
        }

        return idx >= 0 && idx < count;
    }
}
