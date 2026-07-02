using System;
using System.Collections.Generic;
using System.Linq;
using Wholphin.Engine.Data.Enums;

namespace Wholphin.Engine.Recommendation;

/// <summary>The feature view of a catalog item a mood predicate matches against.</summary>
public readonly record struct MoodFeatures(
    HashSet<string> Genres,
    HashSet<string> Tags,
    MediaType MediaType,
    int? Runtime,
    float? Rating);

/// <summary>A named mood and the predicate that decides whether an item belongs to it.</summary>
public record MoodDefinition(string Id, string Title, Func<MoodFeatures, bool> Match);

/// <summary>
/// Pure, testable mood-collection catalogue (Mind Bending, Dark Thrillers, Feel Good, …). Each mood is
/// a predicate over genres/tags/runtime/rating; the service applies them and rotates which appear.
/// Genre names follow TMDB/Jellyfin spelling ("Science Fiction", "Mystery", …), matched case-insensitively.
/// </summary>
public static class MoodCollections
{
    /// <summary>The full mood catalogue.</summary>
    public static readonly IReadOnlyList<MoodDefinition> All = new[]
    {
        new MoodDefinition("mindbending", "Mind Bending",
            f => HasAny(f.Genres, "Science Fiction", "Mystery") && (HasAny(f.Genres, "Thriller", "Mystery") || HasAny(f.Tags, "twist", "dream", "time travel", "psychological"))),
        new MoodDefinition("darkthrillers", "Dark Thrillers",
            f => HasAny(f.Genres, "Thriller", "Crime") && f.Rating is >= 6.5f),
        new MoodDefinition("feelgood", "Feel Good",
            f => HasAny(f.Genres, "Comedy", "Family", "Romance") && f.Rating is >= 6.5f),
        new MoodDefinition("actionpacked", "Action Packed",
            f => HasAny(f.Genres, "Action", "Adventure", "War")),
        new MoodDefinition("crimedrama", "Crime Drama",
            f => f.Genres.Contains("Crime") && f.Genres.Contains("Drama")),
        new MoodDefinition("weekendbinge", "Weekend Binge",
            f => f.MediaType == MediaType.Series && HasAny(f.Genres, "Drama", "Crime", "Mystery", "Thriller", "Fantasy", "Science Fiction")),
        new MoodDefinition("quickwatch", "Quick Watch",
            f => f.MediaType == MediaType.Movie && f.Runtime is > 0 and <= 100),
        new MoodDefinition("acclaimed", "Critically Acclaimed",
            f => f.Rating is >= 8.0f),
    };

    /// <summary>
    /// Picks up to <paramref name="count"/> moods starting from a daily-rotating offset, so the home
    /// surfaces different moods on different days rather than the same first few.
    /// </summary>
    /// <param name="dayOfYear">The current day-of-year (rotation seed).</param>
    /// <param name="count">How many moods to consider this rotation.</param>
    /// <returns>The rotated mood subset, in order.</returns>
    public static IReadOnlyList<MoodDefinition> Rotate(int dayOfYear, int count)
    {
        if (count <= 0 || All.Count == 0)
        {
            return Array.Empty<MoodDefinition>();
        }

        var start = ((dayOfYear % All.Count) + All.Count) % All.Count;
        return Enumerable.Range(0, Math.Min(count, All.Count))
            .Select(i => All[(start + i) % All.Count])
            .ToList();
    }

    private static bool HasAny(HashSet<string> set, params string[] values)
    {
        foreach (var v in values)
        {
            if (set.Contains(v))
            {
                return true;
            }
        }

        return false;
    }
}
