using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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

    // Dimension weights. Each list dimension contributes the MEAN affinity of its leading entries,
    // not the sum: summing let a title with 40 tags and a long cast outscore a sparsely tagged one
    // just for carrying more metadata, while language (one value per title) barely registered —
    // so a Hindi-film viewer was out-scored by well-documented Hollywood and Korean titles.
    private const double GenreWeight = 1.0;
    private const double StudioWeight = 0.4;
    private const double TagWeight = 0.6;
    private const double PersonWeight = 0.8;
    private const double DecadeWeight = 0.3;
    private const double MediaTypeWeight = 0.3;
    private const double LanguageWeight = 1.0;
    private const double RuntimeWeight = 0.2;
    private const double MaturityWeight = 0.2;
    private const double FranchiseWeight = 0.5;

    // Tags and people are long lists in rough order of importance (keywords, billed cast); only the
    // head says what a title is, and averaging over a long tail would dilute it to nothing.
    private const int MaxTags = 8;
    private const int MaxPeople = 5;

    /// <summary>
    /// Computes the raw affinity score of an item: per dimension (genre, studio, tag, cast, decade,
    /// media type, language, runtime, maturity, franchise) the user's mean affinity for what the item
    /// carries, weighted. Positive = aligned, negative = avoided.
    /// </summary>
    /// <param name="affinity">The user's affinity vector.</param>
    /// <param name="item">The catalog item.</param>
    /// <returns>The affinity score (normalize across a candidate set before use).</returns>
    public static double Dot(AffinityVector affinity, CatalogItem item)
    {
        var score = (GenreWeight * Mean(Parse(item.GenresJson), affinity.Genre))
            + (StudioWeight * Mean(Parse(item.StudiosJson), affinity.Studio))
            + (TagWeight * Mean(Parse(item.TagsJson).Take(MaxTags), affinity.Tag))
            + (PersonWeight * Mean(Parse(item.PeopleJson).Take(MaxPeople), affinity.Person))
            + (MediaTypeWeight * affinity.MediaType.GetValueOrDefault(item.MediaType.ToString()));

        if (item.ProductionYear is { } year)
        {
            score += DecadeWeight * affinity.Decade.GetValueOrDefault(Decade(year));
        }

        ForEachScalarFeature(item, (dimension, value) => score += dimension switch
        {
            ScalarDimension.Runtime => RuntimeWeight * affinity.Runtime.GetValueOrDefault(value),
            ScalarDimension.Maturity => MaturityWeight * affinity.Maturity.GetValueOrDefault(value),
            ScalarDimension.Language => LanguageWeight * affinity.Language.GetValueOrDefault(value),
            ScalarDimension.Franchise => FranchiseWeight * affinity.Franchise.GetValueOrDefault(value),
            _ => 0.0,
        });

        return score;
    }

    private static double Mean(IEnumerable<string> keys, Dictionary<string, double> affinity)
    {
        double sum = 0;
        var count = 0;
        foreach (var key in keys)
        {
            sum += affinity.GetValueOrDefault(key);
            count++;
        }

        return count == 0 ? 0 : sum / count;
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
