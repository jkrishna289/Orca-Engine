using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Wholphin.Engine.Llm;

/// <summary>One LLM-proposed title, pre-TMDB-resolution.</summary>
/// <param name="Title">The proposed title as the model wrote it.</param>
/// <param name="Year">The first-release year, when the model supplied one.</param>
/// <param name="Rationale">The model's one-line justification for the pick.</param>
/// <param name="SourceTitle">The watched title that inspired the pick, when attributed.</param>
public sealed record LlmRecommendation(string Title, int? Year, string Rationale, string? SourceTitle);

/// <summary>
/// Defensive parsing for LLM replies that are supposed to be JSON but often aren't quite: models wrap
/// objects in markdown fences, prepend prose, or trail commentary. The ladder is: strip fences →
/// extract the first balanced JSON object (string/escape aware) → parse → validate fields, dropping
/// invalid entries rather than failing the batch. Callers treat a <c>null</c> return as "retry with a
/// corrective message". Pure static — fully unit-testable.
/// </summary>
public static class LlmJsonParser
{
    /// <summary>
    /// Returns the first balanced JSON object in the text, tolerating markdown fences and
    /// surrounding prose, or null when none exists.
    /// </summary>
    /// <param name="raw">The raw model reply.</param>
    /// <returns>The extracted JSON object text, or null.</returns>
    public static string? ExtractJsonObject(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var text = StripFences(raw);
        var start = text.IndexOf('{');
        if (start < 0)
        {
            return null;
        }

        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                    {
                        return text.Substring(start, i - start + 1);
                    }

                    break;
            }
        }

        return null;
    }

    /// <summary>
    /// Runs the full ladder for the recommendation schema
    /// (<c>{"recommendations":[{"title","year","rationale","source_title"}]}</c>). Invalid entries
    /// are dropped individually; null is returned only when nothing usable parses (the caller's
    /// signal to retry with a corrective message).
    /// </summary>
    /// <param name="raw">The raw model reply.</param>
    /// <returns>The valid recommendations, or null when the reply is unusable.</returns>
    public static IReadOnlyList<LlmRecommendation>? TryParseRecommendations(string? raw)
    {
        var json = ExtractJsonObject(raw);
        if (json is null)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("recommendations", out var array)
                || array.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var results = new List<LlmRecommendation>();
            foreach (var entry in array.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var title = ReadString(entry, "title");
                if (string.IsNullOrWhiteSpace(title))
                {
                    continue;
                }

                results.Add(new LlmRecommendation(
                    title.Trim(),
                    ReadYear(entry),
                    ReadString(entry, "rationale")?.Trim() ?? string.Empty,
                    NullIfEmpty(ReadString(entry, "source_title"))));
            }

            return results.Count > 0 ? results : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string StripFences(string raw)
    {
        var text = raw.Trim();
        if (!text.StartsWith("```", StringComparison.Ordinal))
        {
            return text;
        }

        var firstNewline = text.IndexOf('\n');
        if (firstNewline < 0)
        {
            return text;
        }

        text = text[(firstNewline + 1)..];
        var closing = text.LastIndexOf("```", StringComparison.Ordinal);
        return closing >= 0 ? text[..closing].Trim() : text.Trim();
    }

    private static string? ReadString(JsonElement entry, string name)
        => entry.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    private static int? ReadYear(JsonElement entry)
    {
        if (!entry.TryGetProperty("year", out var prop))
        {
            return null;
        }

        // Models sometimes emit years as strings ("2016"); accept both.
        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var n))
        {
            return Plausible(n);
        }

        if (prop.ValueKind == JsonValueKind.String && int.TryParse(prop.GetString(), out var s))
        {
            return Plausible(s);
        }

        return null;

        static int? Plausible(int year) => year is >= 1880 and <= 2100 ? year : null;
    }

    private static string? NullIfEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
