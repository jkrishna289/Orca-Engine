using System.Collections.Generic;
using System.Linq;

namespace Wholphin.Engine.Discovery;

/// <summary>
/// Pipeline stage 2 — hard eligibility rules only. Drops candidates that must never become picks
/// (already in the library, blacklisted, in an active memory cooldown, carrying an avoided genre)
/// and counts every drop by reason for the funnel report. No scoring, no ranking, no writes.
/// </summary>
public class EligibilityFilter
{
    /// <summary>The outcome of an eligibility pass: the surviving candidates plus drop counts by reason.</summary>
    /// <param name="Kept">The candidates that passed every hard rule.</param>
    /// <param name="Dropped">Drop counts keyed by filter reason.</param>
    public sealed record EligibilityResult(
        IReadOnlyList<DiscoveryCandidate> Kept,
        IReadOnlyDictionary<FilterReason, int> Dropped);

    /// <summary>Applies the hard rules to a candidate batch.</summary>
    /// <param name="context">The run context.</param>
    /// <param name="candidates">The aggregated candidates.</param>
    /// <returns>The surviving candidates and the reason-coded drop counts.</returns>
    public EligibilityResult Apply(DiscoveryContext context, IReadOnlyList<DiscoveryCandidate> candidates)
    {
        var kept = new List<DiscoveryCandidate>(candidates.Count);
        var dropped = new Dictionary<FilterReason, int>();

        foreach (var candidate in candidates)
        {
            var reason = Evaluate(context, candidate);
            if (reason is { } r)
            {
                dropped[r] = dropped.GetValueOrDefault(r) + 1;
            }
            else
            {
                kept.Add(candidate);
            }
        }

        return new EligibilityResult(kept, dropped);
    }

    private static FilterReason? Evaluate(DiscoveryContext context, DiscoveryCandidate candidate)
    {
        var result = candidate.Result;
        if (result.TmdbId <= 0 || string.IsNullOrWhiteSpace(result.Title))
        {
            return FilterReason.InvalidId;
        }

        if (context.LibraryTmdbIds.Contains(result.TmdbId))
        {
            return FilterReason.InLibrary;
        }

        if (context.BlacklistedTmdbIds.Contains(result.TmdbId))
        {
            return FilterReason.Blacklisted;
        }

        if (context.Memory.TryGetValue((result.TmdbId, result.MediaType), out var memory)
            && InterestModel.IsInCooldown(memory, context.Now))
        {
            return FilterReason.Cooldown;
        }

        if (context.Profile is { AvoidGenres.Count: > 0 } profile
            && result.Genres.Any(g => profile.AvoidGenres.Contains(g, System.StringComparer.OrdinalIgnoreCase)))
        {
            return FilterReason.AvoidGenre;
        }

        return null;
    }
}
