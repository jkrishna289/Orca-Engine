using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Wholphin.Engine.Data.Entities;

namespace Wholphin.Engine.Personalization;

/// <summary>
/// Reads the denormalized feature arrays off a <see cref="CatalogItem"/> and scores an item
/// against an <see cref="AffinityVector"/>. Shared by the personalization and recommendation
/// engines so the feature meaning never drifts between "learning" and "scoring".
/// </summary>
public static class CatalogFeatures
{
    /// <summary>Parses a JSON string-array column (genres/studios/tags) into a list.</summary>
    /// <param name="json">The JSON array, or null.</param>
    /// <returns>The parsed values (empty if null/invalid).</returns>
    public static IReadOnlyList<string> Parse(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return Array.Empty<string>();
        }

        try
        {
            return JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>Returns the decade bucket label for a production year (e.g. 1994 → "1990").</summary>
    /// <param name="year">The production year.</param>
    /// <returns>The decade label.</returns>
    public static string Decade(int year) => ((year / 10) * 10).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Computes the raw affinity score of an item: the sum of the user's affinity for every
    /// feature the item carries (genre, studio, tag, decade, media type).
    /// </summary>
    /// <param name="affinity">The user's affinity vector.</param>
    /// <param name="item">The catalog item.</param>
    /// <returns>The dot-product affinity (unbounded; normalize across a candidate set before use).</returns>
    public static double Dot(AffinityVector affinity, CatalogItem item)
    {
        double score = 0;

        foreach (var g in Parse(item.GenresJson))
        {
            score += affinity.Genre.GetValueOrDefault(g);
        }

        foreach (var s in Parse(item.StudiosJson))
        {
            score += affinity.Studio.GetValueOrDefault(s);
        }

        foreach (var t in Parse(item.TagsJson))
        {
            score += affinity.Tag.GetValueOrDefault(t);
        }

        foreach (var p in Parse(item.PeopleJson))
        {
            score += affinity.Person.GetValueOrDefault(p);
        }

        if (item.ProductionYear is { } year)
        {
            score += affinity.Decade.GetValueOrDefault(Decade(year));
        }

        score += affinity.MediaType.GetValueOrDefault(item.MediaType.ToString());

        return score;
    }
}
