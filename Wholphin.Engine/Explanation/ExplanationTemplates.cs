using System;
using System.Collections.Generic;
using System.Linq;
using Wholphin.Engine.Data.Entities;
using Wholphin.Engine.Personalization;

namespace Wholphin.Engine.Explanation;

/// <summary>
/// Pure, deterministic reason-string builders — the single source of truth for recommendation
/// "why" copy, so every surface (local recommender, discovery pipeline) speaks the same vocabulary
/// and a good reason exists even when the optional LLM re-ranker is off. Stateless + unit-testable.
/// </summary>
public static class ExplanationTemplates
{
    /// <summary>Minimum community rating for the "highly rated" fallback.</summary>
    private const double HighlyRatedThreshold = 7.5;

    /// <summary>
    /// The reason for a taste-matched discovery candidate: the genres it shares with the user's top
    /// genres, or a generic fallback when there is no overlap to name.
    /// </summary>
    /// <param name="candidateGenres">The candidate's genres.</param>
    /// <param name="topGenres">The user's top genres.</param>
    /// <returns>A short reason string.</returns>
    public static string TasteMatch(IReadOnlyList<string> candidateGenres, IReadOnlyList<string> topGenres)
    {
        var shared = candidateGenres
            .Where(g => topGenres.Contains(g, StringComparer.OrdinalIgnoreCase))
            .Take(2)
            .Select(g => g.ToLowerInvariant())
            .ToList();
        return shared.Count > 0
            ? $"Matches your taste for {string.Join(" and ", shared)}"
            : "Matches your taste";
    }

    /// <summary>
    /// The reason for a personalized library recommendation: the strongest signal that put the item
    /// on the list — a loved genre, a liked person, otherwise its quality. Always returns something
    /// sensible so every card can carry a subtitle without the LLM.
    /// </summary>
    /// <param name="item">The recommended item.</param>
    /// <param name="affinity">The user's affinity vector.</param>
    /// <returns>A short reason string.</returns>
    public static string Recommendation(CatalogItem item, AffinityVector affinity)
    {
        var (bestGenre, bestGenreAffinity) = Strongest(CatalogFeatures.Parse(item.GenresJson), affinity.Genre);
        var (bestPerson, bestPersonAffinity) = Strongest(CatalogFeatures.Parse(item.PeopleJson), affinity.Person);

        if (bestGenre is not null && bestGenreAffinity >= bestPersonAffinity)
        {
            return $"Because you enjoy {bestGenre}";
        }

        if (bestPerson is not null)
        {
            return $"Because you like {StripRole(bestPerson)}";
        }

        return (item.CommunityRating ?? 0) >= HighlyRatedThreshold ? "Highly rated" : "Picked for you";
    }

    /// <summary>Returns the feature with the highest positive affinity, or (null, 0) when none is positive.</summary>
    private static (string? Feature, double Affinity) Strongest(IReadOnlyList<string> features, IReadOnlyDictionary<string, double> dimension)
    {
        string? best = null;
        var bestAffinity = 0.0;
        foreach (var feature in features)
        {
            var affinity = dimension.GetValueOrDefault(feature);
            if (affinity > bestAffinity)
            {
                bestAffinity = affinity;
                best = feature;
            }
        }

        return (best, bestAffinity);
    }

    // People are stored role-prefixed ("Director:Christopher Nolan"); reasons show the name.
    private static string StripRole(string value)
    {
        var idx = value.IndexOf(':');
        return (idx >= 0 && idx < value.Length - 1 ? value[(idx + 1)..] : value).Trim();
    }
}
