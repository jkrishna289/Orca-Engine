using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wholphin.Engine.Caching;
using Wholphin.Engine.Data;
using Wholphin.Engine.Data.Entities;
using Wholphin.Engine.Data.Enums;
using Wholphin.Engine.Diagnostics;
using Wholphin.Engine.Llm;
using Wholphin.Engine.Personalization;

namespace Wholphin.Engine.Metadata;

/// <summary>
/// Default <see cref="IContentWarningProvider"/>. Asks Groq for the title's content advisories, hardens
/// and re-sorts the reply into a deterministic safety-first order, and stores the result durably on the
/// catalog row (so it is computed once per movie/series). Groq is the sole source — when it is
/// unavailable or the call fails, the result is "no warnings" and nothing is invented.
/// </summary>
public class ContentWarningProvider : IContentWarningProvider
{
    private const int MaxWarnings = 6;
    private const int MaxNoteLength = 120;
    private const int MaxSummaryLength = 120;
    private const int OverviewLimit = 500;

    private static readonly TimeSpan WarningsTtl = TimeSpan.FromDays(30);
    private static readonly TimeSpan EmptyTtl = TimeSpan.FromHours(6);

    private static readonly HashSet<string> Severities = new(StringComparer.OrdinalIgnoreCase) { "mild", "moderate", "severe" };

    private const string SystemPrompt =
        """
        You are a content-safety analyst for a personal media server. Given a film or TV title and its
        metadata, list the sensitive-content advisories a viewer should know BEFORE watching, so nothing
        catches them off guard.

        Reply ONLY with compact JSON of this exact shape:
        {"hasWarnings":<true|false>,"summary":"<one plain sentence, max 90 chars, or empty>","warnings":[{"category":"<exact category from the list>","severity":"mild|moderate|severe","note":"<short, spoiler-free description, max 90 chars>"}]}

        Use ONLY these categories, spelled EXACTLY as shown, and output any that apply in THIS numbered order (health and trauma first, language last):
        1. Photosensitivity
        2. Suicide and self-harm
        3. Sexual violence
        4. Harm to children
        5. Animal cruelty
        6. Graphic violence and gore
        7. Death, grief and medical trauma
        8. Sexual content and nudity
        9. Substance use and addiction
        10. Disturbing and frightening imagery
        11. Strong language and slurs
        12. Other sensitive themes

        Rules:
        - Include ONLY categories that genuinely occur in THIS specific title. Omit the rest; never pad.
        - Favor advisories a viewer would NOT anticipate from the title, genre, or age rating alone - aim for "I didn't know that", not restating the obvious.
        - Each note names the KIND of content only. No plot details, character names, episode/scene numbers, or outcomes. Never spoil.
        - Base every advisory on well-established facts about this exact title. NEVER invent. If you don't recognise the title, infer only what its genre and age rating strongly imply, mark those "mild", and keep the list short - or return {"hasWarnings":false,"summary":"","warnings":[]} if unsure.
        - Photosensitivity means strobe/flashing-light sequences that pose a seizure risk; include only when known.
        - At most 6 warnings. Output JSON only - no prose, no markdown.
        """;

    private readonly ILlmProvider _llm;
    private readonly ICache _cache;
    private readonly IWholphinDbContextFactory _factory;
    private readonly IEngineMetrics _metrics;
    private readonly ILogger<ContentWarningProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ContentWarningProvider"/> class.
    /// </summary>
    public ContentWarningProvider(
        ILlmProvider llm,
        ICache cache,
        IWholphinDbContextFactory factory,
        IEngineMetrics metrics,
        ILogger<ContentWarningProvider> logger)
    {
        _llm = llm;
        _cache = cache;
        _factory = factory;
        _metrics = metrics;
        _logger = logger;
    }

    private static bool FeatureEnabled => Plugin.Instance?.Configuration?.FeatureContentWarnings == true;

    /// <inheritdoc />
    public async Task<ContentWarnings> GetAsync(CatalogItem item, CancellationToken ct = default)
    {
        var cacheKey = $"warnings:{item.Id}";
        if (_cache.TryGet<ContentWarnings>(cacheKey, out var cached) && cached is not null)
        {
            return cached;
        }

        // Durable, per-title result already computed (survives restarts).
        if (!string.IsNullOrEmpty(item.ContentWarningsJson))
        {
            var stored = Deserialize(item.ContentWarningsJson);
            _cache.Set(cacheKey, stored, WarningsTtl);
            return stored;
        }

        // Groq is the sole source; without it we return "no warnings" and do NOT persist (retry later).
        if (!_llm.IsConfigured || !FeatureEnabled)
        {
            _cache.Set(cacheKey, ContentWarnings.None, EmptyTtl);
            return ContentWarnings.None;
        }

        var (result, ok) = await ProduceAsync(item, ct).ConfigureAwait(false);

        // Always stamp "attempted" so the background enricher rotates past this row; persist the JSON only
        // on a genuine Groq result (so a transient failure is retried on-demand at playback).
        await PersistAsync(item.Id, ok ? Serialize(result) : null, ct).ConfigureAwait(false);
        _cache.Set(cacheKey, result, ok && result.HasWarnings ? WarningsTtl : EmptyTtl);
        return result;
    }

    private async Task<(ContentWarnings Result, bool Ok)> ProduceAsync(CatalogItem item, CancellationToken ct)
    {
        try
        {
            var reply = await _llm.CompleteAsync(SystemPrompt, BuildPrompt(item), ct).ConfigureAwait(false);
            if (reply is null)
            {
                _metrics.Increment("warnings.llm.error");
                return (ContentWarnings.None, false);
            }

            var parsed = Parse(reply);
            _metrics.Increment(parsed.HasWarnings ? "warnings.llm.ok" : "warnings.llm.empty");
            return (parsed, true);
        }
        catch (Exception ex)
        {
            _metrics.Increment("warnings.llm.error");
            _logger.LogWarning(ex, "Orca Engine: content-warning LLM call failed for {Title}.", item.Title);
            return (ContentWarnings.None, false);
        }
    }

    private static string BuildPrompt(CatalogItem item)
    {
        var type = item.MediaType == MediaType.Series ? "TV series" : "movie";
        var year = item.ProductionYear is { } y ? $" ({y.ToString(CultureInfo.InvariantCulture)})" : string.Empty;
        var rating = string.IsNullOrWhiteSpace(item.OfficialRating) ? "unknown" : item.OfficialRating!.Trim();
        var genres = string.Join(", ", CatalogFeatures.Parse(item.GenresJson).Take(5));
        var tags = string.Join(", ", CatalogFeatures.Parse(item.TagsJson).Take(8));
        var overview = string.IsNullOrWhiteSpace(item.Overview)
            ? string.Empty
            : (item.Overview!.Length > OverviewLimit ? item.Overview[..OverviewLimit] : item.Overview);
        return $"{type} title: \"{item.Title}\"{year}. Age rating: {rating}. Genres: {genres}. Themes: {tags}. Overview: {overview}";
    }

    private static ContentWarnings Parse(string reply)
    {
        if (string.IsNullOrWhiteSpace(reply))
        {
            return ContentWarnings.None;
        }

        try
        {
            using var doc = JsonDocument.Parse(reply);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return ContentWarnings.None;
            }

            var warnings = new List<ContentWarning>();
            if (root.TryGetProperty("warnings", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in arr.EnumerateArray())
                {
                    if (el.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var category = GetString(el, "category");
                    if (ContentWarningCategories.OrderOf(category) == int.MaxValue)
                    {
                        continue; // unknown category → drop
                    }

                    // De-dupe by category (keep the first note for each).
                    if (warnings.Any(w => string.Equals(w.Category, category, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    var severity = GetString(el, "severity");
                    if (severity is null || !Severities.Contains(severity))
                    {
                        severity = "moderate";
                    }

                    var note = GetString(el, "note")?.Trim() ?? string.Empty;
                    if (note.Length > MaxNoteLength)
                    {
                        note = note[..MaxNoteLength].TrimEnd() + "…";
                    }

                    warnings.Add(new ContentWarning
                    {
                        Category = ContentWarningCategories.Ordered[ContentWarningCategories.OrderOf(category)],
                        Severity = severity.ToLowerInvariant(),
                        Note = note,
                    });
                }
            }

            // Deterministic safety-first order regardless of what the model returned, then cap.
            warnings = warnings
                .OrderBy(w => ContentWarningCategories.OrderOf(w.Category))
                .Take(MaxWarnings)
                .ToList();

            var summary = GetString(root, "summary")?.Trim() ?? string.Empty;
            if (summary.Length > MaxSummaryLength)
            {
                summary = summary[..MaxSummaryLength].TrimEnd() + "…";
            }

            return new ContentWarnings
            {
                HasWarnings = warnings.Count > 0,
                Summary = warnings.Count > 0 ? summary : string.Empty,
                Warnings = warnings,
            };
        }
        catch (JsonException)
        {
            return ContentWarnings.None;
        }
    }

    private static string? GetString(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private async Task PersistAsync(long id, string? json, CancellationToken ct)
    {
        try
        {
            await using var db = _factory.Create();
            var row = await db.CatalogItems.FirstOrDefaultAsync(c => c.Id == id, ct).ConfigureAwait(false);
            if (row is null)
            {
                return;
            }

            if (json is not null)
            {
                row.ContentWarningsJson = json;
            }

            row.ContentWarningsSyncedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Orca Engine: failed to persist content warnings for catalog {Id}.", id);
        }
    }

    private static string Serialize(ContentWarnings warnings) => JsonSerializer.Serialize(warnings);

    private static ContentWarnings Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<ContentWarnings>(json) ?? ContentWarnings.None;
        }
        catch (JsonException)
        {
            return ContentWarnings.None;
        }
    }
}
