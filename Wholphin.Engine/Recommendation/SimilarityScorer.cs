using System;
using System.Collections.Generic;
using Wholphin.Engine.Data.Entities;
using Wholphin.Engine.Personalization;

namespace Wholphin.Engine.Recommendation;

/// <summary>
/// Pure, content-based item-to-item similarity. Blends set overlap (genres/tags/studios) with
/// media-type, decade and rating closeness into a single 0..1 score. Stateless and deterministic
/// so it can be unit-tested in isolation; the source of truth for "how alike are two titles".
/// </summary>
public static class SimilarityScorer
{
    // Weights sum to 1.0 so the score stays in [0, 1]. Genre dominates, then shared people
    // (cast/director — a strong "more like this" signal) and themes (tags), then keeping the same
    // medium, with studio/era/rating as lighter tie-breakers.
    private const double GenreWeight = 0.34;
    private const double PeopleWeight = 0.18;
    private const double TagWeight = 0.15;
    private const double MediaTypeWeight = 0.12;
    private const double StudioWeight = 0.09;
    private const double YearWeight = 0.08;
    private const double RatingWeight = 0.04;

    /// <summary>Years apart at which decade-closeness decays to zero.</summary>
    private const double YearSpan = 30.0;

    /// <summary>Computes the similarity of two catalog items (0 = unrelated, 1 = identical features).</summary>
    /// <param name="a">The first item.</param>
    /// <param name="b">The second item.</param>
    /// <returns>A similarity score in [0, 1].</returns>
    public static double Score(CatalogItem a, CatalogItem b)
    {
        var genre = Jaccard(ToSet(a.GenresJson), ToSet(b.GenresJson));
        var tag = Jaccard(ToSet(a.TagsJson), ToSet(b.TagsJson));
        var studio = Jaccard(ToSet(a.StudiosJson), ToSet(b.StudiosJson));
        var people = Jaccard(ToSet(a.PeopleJson), ToSet(b.PeopleJson));

        // Similarity requires real content overlap. Without a shared genre/tag/studio/person, matching
        // media-type/era/rating alone does NOT make two titles "alike" — score them as unrelated.
        if (genre <= 0 && tag <= 0 && studio <= 0 && people <= 0)
        {
            return 0.0;
        }

        var mediaType = a.MediaType == b.MediaType ? 1.0 : 0.0;
        var year = YearCloseness(a.ProductionYear, b.ProductionYear);
        var rating = RatingCloseness(a.CommunityRating, b.CommunityRating);

        return (GenreWeight * genre)
               + (PeopleWeight * people)
               + (TagWeight * tag)
               + (MediaTypeWeight * mediaType)
               + (StudioWeight * studio)
               + (YearWeight * year)
               + (RatingWeight * rating);
    }

    private static HashSet<string> ToSet(string? json)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in CatalogFeatures.Parse(json))
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                set.Add(value);
            }
        }

        return set;
    }

    private static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0)
        {
            return 0.0;
        }

        var intersection = 0;
        foreach (var value in a)
        {
            if (b.Contains(value))
            {
                intersection++;
            }
        }

        var union = a.Count + b.Count - intersection;
        return union == 0 ? 0.0 : intersection / (double)union;
    }

    private static double YearCloseness(int? a, int? b)
    {
        if (a is not { } ya || b is not { } yb)
        {
            return 0.0;
        }

        return Math.Max(0.0, 1.0 - (Math.Abs(ya - yb) / YearSpan));
    }

    private static double RatingCloseness(float? a, float? b)
    {
        if (a is not { } ra || b is not { } rb)
        {
            return 0.0;
        }

        return Math.Max(0.0, 1.0 - (Math.Abs(ra - rb) / 10.0));
    }
}
