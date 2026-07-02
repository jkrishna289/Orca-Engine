using System;
using System.Collections.Generic;
using System.Linq;

namespace Wholphin.Engine.Recommendation;

/// <summary>A directed genre association: from a source genre to a related genre.</summary>
/// <param name="Genre">The related genre.</param>
/// <param name="Weight">Association strength P(related | source) in [0, 1].</param>
/// <param name="Count">How many catalog items contain both genres.</param>
public record GenreLink(string Genre, double Weight, int Count);

/// <summary>
/// Pure, offline genre co-occurrence graph. For each genre it ranks the genres that most often
/// appear alongside it, weighted by the conditional probability P(related | source) so a frequent
/// genre doesn't swamp the associations of a rare one. Stateless and deterministic (unit-testable);
/// the source of truth for "which genres relate to which".
/// </summary>
public static class GenreGraph
{
    /// <summary>Builds the association graph from each item's genre list.</summary>
    /// <param name="itemGenres">Per-item genre lists.</param>
    /// <param name="topPerGenre">Maximum related genres to keep per source genre.</param>
    /// <returns>Source genre → ranked related genres.</returns>
    public static Dictionary<string, List<GenreLink>> Build(IEnumerable<IReadOnlyList<string>> itemGenres, int topPerGenre = 10)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var co = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);

        foreach (var genres in itemGenres)
        {
            var distinct = genres
                .Where(g => !string.IsNullOrWhiteSpace(g))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var a in distinct)
            {
                counts[a] = counts.GetValueOrDefault(a) + 1;
                if (!co.TryGetValue(a, out var inner))
                {
                    inner = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    co[a] = inner;
                }

                foreach (var b in distinct)
                {
                    if (!string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
                    {
                        inner[b] = inner.GetValueOrDefault(b) + 1;
                    }
                }
            }
        }

        var result = new Dictionary<string, List<GenreLink>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (source, related) in co)
        {
            var total = counts[source];
            result[source] = related
                .Select(kv => new GenreLink(kv.Key, total > 0 ? Math.Round(kv.Value / (double)total, 4) : 0, kv.Value))
                .OrderByDescending(l => l.Weight)
                .ThenByDescending(l => l.Count)
                .Take(topPerGenre)
                .ToList();
        }

        return result;
    }
}
