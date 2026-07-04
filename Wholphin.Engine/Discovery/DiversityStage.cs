using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Wholphin.Engine.Discovery;

/// <summary>
/// Pipeline stage 4 — post-ranking reshuffle only. Greedy MMR-style re-order (the same idea as
/// <c>ContentRecommender.DiversityRerank</c>) that spreads primary genres and breaks up
/// franchises (title-stem repeats), recording the applied penalty on each score. Reorders and
/// penalizes — never drops; the cut is selection's job.
/// </summary>
public class DiversityStage
{
    private const int GenreFreeCount = 2;
    private const double GenrePenaltyStep = 0.05;
    private const double FranchisePenaltyStep = 0.15;

    private readonly Ranking.IScoringPolicy _scoring;

    /// <summary>Initializes a new instance of the <see cref="DiversityStage"/> class.</summary>
    /// <param name="scoring">The shared scoring policy (for recomputing scores after a penalty).</param>
    public DiversityStage(Ranking.IScoringPolicy scoring) => _scoring = scoring;

    /// <summary>The outcome of the reshuffle.</summary>
    /// <param name="Ranked">The candidates in their final diversity-aware order.</param>
    /// <param name="Reordered">How many candidates moved from their pre-diversity rank position.</param>
    public sealed record DiversityResult(List<ScoredCandidate> Ranked, int Reordered);

    /// <summary>Reshuffles a scored batch for variety.</summary>
    /// <param name="scored">The scored candidates (any order).</param>
    /// <returns>The reordered candidates with diversity penalties folded into their final scores.</returns>
    public DiversityResult Apply(IReadOnlyList<ScoredCandidate> scored)
    {
        var remaining = scored.OrderByDescending(s => s.Score.Final).ToList();
        var initialOrder = remaining
            .Select((s, i) => (s, i))
            .ToDictionary(x => x.s, x => x.i);

        var ranked = new List<ScoredCandidate>(remaining.Count);
        var genreCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var stemCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        while (remaining.Count > 0)
        {
            ScoredCandidate? best = null;
            var bestScore = double.NegativeInfinity;
            var bestPenalty = 0.0;
            foreach (var candidate in remaining)
            {
                var penalty = Penalty(candidate, genreCounts, stemCounts);
                var adjusted = candidate.Score.Final - penalty;
                if (adjusted > bestScore)
                {
                    best = candidate;
                    bestScore = adjusted;
                    bestPenalty = penalty;
                }
            }

            var pick = best!;
            remaining.Remove(pick);
            if (bestPenalty > 0)
            {
                pick.Score.DiversityPenalty = Math.Round(bestPenalty, 4);
                pick.Score.Final = CandidateScorer.ComputeFinal(pick.Score, _scoring.Discovery);
            }

            ranked.Add(pick);
            if (PrimaryGenre(pick) is { } genre)
            {
                genreCounts[genre] = genreCounts.GetValueOrDefault(genre) + 1;
            }

            var stem = TitleStem(pick.Candidate.Result.Title);
            if (stem.Length > 0)
            {
                stemCounts[stem] = stemCounts.GetValueOrDefault(stem) + 1;
            }
        }

        var reordered = ranked.Where((s, i) => initialOrder[s] != i).Count();
        return new DiversityResult(ranked, reordered);
    }

    private static double Penalty(
        ScoredCandidate candidate,
        Dictionary<string, int> genreCounts,
        Dictionary<string, int> stemCounts)
    {
        var penalty = 0.0;
        if (PrimaryGenre(candidate) is { } genre)
        {
            var count = genreCounts.GetValueOrDefault(genre);
            if (count >= GenreFreeCount)
            {
                penalty += GenrePenaltyStep * (count - GenreFreeCount + 1);
            }
        }

        var stem = TitleStem(candidate.Candidate.Result.Title);
        if (stem.Length > 0 && stemCounts.TryGetValue(stem, out var stemCount))
        {
            penalty += FranchisePenaltyStep * stemCount;
        }

        return penalty;
    }

    private static string? PrimaryGenre(ScoredCandidate candidate)
        => candidate.Candidate.Result.Genres.Count > 0 ? candidate.Candidate.Result.Genres[0] : null;

    /// <summary>
    /// A crude franchise key: the leading alphabetic words of the title with numerals, "Part"
    /// markers and separators dropped ("Mission: Impossible 2" and "Mission: Impossible – Fallout"
    /// share a stem). Spread-based, so imperfect merges only nudge the order, never ban a title.
    /// </summary>
    private static string TitleStem(string title)
    {
        var sb = new StringBuilder(title.Length);
        foreach (var ch in title)
        {
            if (char.IsLetter(ch) || char.IsWhiteSpace(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
        }

        var words = sb.ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w is not ("part" or "chapter" or "volume"))
            .Take(3);
        return string.Join(' ', words);
    }
}
