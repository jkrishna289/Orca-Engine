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

    /// <summary>Buckets a runtime (minutes) into a coarse length preference, or null when unknown.</summary>
    /// <param name="minutes">The runtime in minutes.</param>
    /// <returns>short/medium/long/epic, or null.</returns>
    public static string? RuntimeBucket(int? minutes) => minutes switch
    {
        null or <= 0 => null,
        < 90 => "short",
        < 120 => "medium",
        < 150 => "long",
        _ => "epic",
    };

    /// <summary>
    /// Invokes <paramref name="accumulate"/> for each single-valued taste feature an item carries
    /// (runtime bucket, maturity rating, original language, franchise). Shared by the learning
    /// (personalization) and scoring paths so the feature meaning never drifts.
    /// </summary>
    /// <param name="item">The catalog item.</param>
    /// <param name="accumulate">Callback of (dimension name, feature value).</param>
    public static void ForEachScalarFeature(CatalogItem item, Action<ScalarDimension, string> accumulate)
    {
        if (RuntimeBucket(item.RuntimeMinutes) is { } bucket)
        {
            accumulate(ScalarDimension.Runtime, bucket);
        }

        if (!string.IsNullOrWhiteSpace(item.OfficialRating))
        {
            accumulate(ScalarDimension.Maturity, item.OfficialRating);
        }

        if (!string.IsNullOrWhiteSpace(item.OriginalLanguage))
        {
            accumulate(ScalarDimension.Language, item.OriginalLanguage);
        }

        if (!string.IsNullOrWhiteSpace(item.CollectionName))
        {
            accumulate(ScalarDimension.Franchise, item.CollectionName);
        }
    }

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

        // Scalar taste dimensions (runtime length, maturity, language, franchise).
        var scalar = 0.0;
        ForEachScalarFeature(item, (dimension, value) => scalar += dimension switch
        {
            ScalarDimension.Runtime => affinity.Runtime.GetValueOrDefault(value),
            ScalarDimension.Maturity => affinity.Maturity.GetValueOrDefault(value),
            ScalarDimension.Language => affinity.Language.GetValueOrDefault(value),
            ScalarDimension.Franchise => affinity.Franchise.GetValueOrDefault(value),
            _ => 0.0,
        });

        return score + scalar;
    }
}

/// <summary>The single-valued (non-array) taste dimensions of a catalog item.</summary>
public enum ScalarDimension
{
    /// <summary>Runtime-length bucket.</summary>
    Runtime,

    /// <summary>Official maturity/age rating.</summary>
    Maturity,

    /// <summary>Original language.</summary>
    Language,

    /// <summary>Collection/franchise.</summary>
    Franchise,
}
