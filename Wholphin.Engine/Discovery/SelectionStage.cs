using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Wholphin.Engine.Data.Enums;

namespace Wholphin.Engine.Discovery;

/// <summary>
/// Pipeline stage 5 — final cuts only. Applies the taste-floor thresholds, reserves the
/// exploration quota, and takes the per-kind top-K (Because You Watched per seed, You Might Like,
/// Trending, Country). Everything that survives becomes a pick proposal for the persistence
/// stage; nothing here scores, reorders beyond its cuts, or writes.
/// </summary>
public class SelectionStage
{
    /// <summary>Taste-match picks selected per pull (exploration quota carved from the same budget).</summary>
    public const int TasteMatchCap = 20;

    /// <summary>"Because You Watched" picks selected per seed.</summary>
    public const int MaxPerSeed = 4;

    /// <summary>Unseeded LLM picks kept per pull (~one You-Might-Like row's worth).</summary>
    public const int LlmPickCap = 12;

    /// <summary>Global trending picks kept per pull.</summary>
    public const int TrendingCap = 20;

    /// <summary>Per-country picks kept per pull.</summary>
    public const int CountryCap = 20;

    /// <summary>Minimum TMDB rating for an exploration pick (quality floor replaces the taste floor).</summary>
    public const double ExplorationQualityFloor = 6.5;

    /// <summary>The outcome of selection.</summary>
    /// <param name="Picks">The pick proposals for persistence.</param>
    /// <param name="BelowThreshold">How many ranked candidates fell below the selection thresholds/caps.</param>
    public sealed record SelectionResult(List<SelectedPick> Picks, int BelowThreshold);

    /// <summary>Selects the final picks from the diversity-ranked candidates.</summary>
    /// <param name="context">The run context.</param>
    /// <param name="ranked">The candidates in diversity-aware rank order.</param>
    /// <param name="tasteFloor">The taste threshold matching the run's vector kind.</param>
    /// <returns>The pick proposals plus the below-threshold count for the funnel.</returns>
    public SelectionResult Select(DiscoveryContext context, IReadOnlyList<ScoredCandidate> ranked, double tasteFloor)
    {
        var picks = new List<SelectedPick>();
        var byKind = ranked.GroupBy(s => s.Candidate.Kind).ToDictionary(g => g.Key, g => g.ToList());

        // Because You Watched: the seed relation is the justification, so no centroid taste floor —
        // a mode-specific match (one taste among many) must not be killed by the taste average.
        if (byKind.TryGetValue(DiscoveryPickKind.BecauseYouWatched, out var seeded))
        {
            foreach (var group in seeded.Where(s => s.Candidate.Seed is not null).GroupBy(s => s.Candidate.Seed!.TmdbId))
            {
                foreach (var scored in group.Where(s => s.Score.Final > 0).Take(MaxPerSeed))
                {
                    var seed = scored.Candidate.Seed!;
                    picks.Add(new SelectedPick(
                        scored,
                        DiscoveryPickKind.BecauseYouWatched,
                        scored.Candidate.LlmRationale ?? $"Because you watched {seed.Title}",
                        seed.TmdbId,
                        seed.Title,
                        Country: null));
                }
            }
        }

        // LLM picks: individually curated by the model, so like BYW they skip the centroid taste
        // floor — a small-batch cosine calibrated for 40-result discover pages would strangle
        // exactly the picks whose quality comes from the model's open-world judgment.
        if (byKind.TryGetValue(DiscoveryPickKind.LlmPick, out var llmPicks))
        {
            foreach (var scored in llmPicks.Where(s => s.Score.Final > 0).Take(LlmPickCap))
            {
                picks.Add(new SelectedPick(
                    scored,
                    DiscoveryPickKind.LlmPick,
                    scored.Candidate.LlmRationale ?? "Picked for you by AI",
                    SeedTmdbId: null,
                    SeedTitle: null,
                    Country: null));
            }
        }

        // You Might Like: taste-floored picks plus a reserved exploration quota (novel, high
        // quality, honestly labeled) so the row never collapses into an echo chamber.
        if (byKind.TryGetValue(DiscoveryPickKind.TasteMatch, out var tasteMatches))
        {
            var explorationSlots = (int)Math.Floor(TasteMatchCap * context.Tuning.ExplorationFraction);
            var qualified = tasteMatches.Where(s => s.Score.Taste >= tasteFloor && s.Score.Final > 0).ToList();

            var main = qualified.Take(TasteMatchCap - explorationSlots).ToList();
            foreach (var scored in main)
            {
                picks.Add(new SelectedPick(
                    scored,
                    DiscoveryPickKind.TasteMatch,
                    TasteReason(context, scored),
                    SeedTmdbId: null,
                    SeedTitle: null,
                    Country: null));
            }

            var chosen = new HashSet<ScoredCandidate>(main);
            var exploration = tasteMatches
                .Where(s => !chosen.Contains(s)
                    && s.Score.Novelty > 0
                    && (s.Candidate.Result.CommunityRating ?? 0) >= ExplorationQualityFloor)
                .OrderByDescending(s => s.Score.Novelty)
                .Take(explorationSlots);
            foreach (var scored in exploration)
            {
                picks.Add(new SelectedPick(
                    scored,
                    DiscoveryPickKind.Exploration,
                    "Something different — highly rated",
                    SeedTmdbId: null,
                    SeedTitle: null,
                    Country: null));
            }
        }

        if (byKind.TryGetValue(DiscoveryPickKind.Trending, out var trending))
        {
            foreach (var scored in trending.Take(TrendingCap))
            {
                picks.Add(new SelectedPick(scored, DiscoveryPickKind.Trending, "Trending this week", null, null, Country: null));
            }
        }

        if (byKind.TryGetValue(DiscoveryPickKind.Country, out var country) && context.CountryCode is { } cc)
        {
            foreach (var scored in country.Take(CountryCap))
            {
                picks.Add(new SelectedPick(scored, DiscoveryPickKind.Country, $"Popular in {CountryName(cc)}", null, null, cc));
            }
        }

        return new SelectionResult(picks, Math.Max(0, ranked.Count - picks.Count));
    }

    /// <summary>The display name for a country code ("IN" → "India"), falling back to the code itself.</summary>
    /// <param name="countryCode">The ISO-3166-1 code.</param>
    /// <returns>The English display name.</returns>
    public static string CountryName(string countryCode)
    {
        try
        {
            return new RegionInfo(countryCode).EnglishName;
        }
        catch (ArgumentException)
        {
            return countryCode;
        }
    }

    private static string TasteReason(DiscoveryContext context, ScoredCandidate scored)
        => Explanation.ExplanationTemplates.TasteMatch(
            scored.Candidate.Result.Genres,
            context.Profile?.TopGenres ?? new List<string>());
}

/// <summary>A selected pick proposal — everything the persistence stage needs to write one justification row.</summary>
/// <param name="Scored">The scored candidate.</param>
/// <param name="Kind">The final justification kind (selection may promote TasteMatch → Exploration).</param>
/// <param name="Reason">The human-readable justification shown on the card.</param>
/// <param name="SeedTmdbId">The seed's TMDB id, for seeded picks.</param>
/// <param name="SeedTitle">The seed's title, for row headings.</param>
/// <param name="Country">The country code, for country picks.</param>
public sealed record SelectedPick(
    ScoredCandidate Scored,
    DiscoveryPickKind Kind,
    string Reason,
    int? SeedTmdbId,
    string? SeedTitle,
    string? Country);
