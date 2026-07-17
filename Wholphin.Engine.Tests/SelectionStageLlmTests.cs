using System.Collections.Generic;
using System.Linq;
using Wholphin.Engine.Data.Enums;
using Wholphin.Engine.Discovery;
using Wholphin.Engine.Integrations.Jellyseerr;
using Wholphin.Engine.Personalization;
using Xunit;

namespace Wholphin.Engine.Tests;

/// <summary>Unit tests for the selection stage's LLM-pick branch and rationale carriage.</summary>
public class SelectionStageLlmTests
{
    private static ScoredCandidate Llm(int tmdbId, string title, double finalScore, string? rationale, TasteSeed? seed = null)
    {
        var candidate = new DiscoveryCandidate
        {
            Result = new DiscoverResult { TmdbId = tmdbId, MediaType = MediaType.Movie, Title = title },
            Kind = seed is null ? DiscoveryPickKind.LlmPick : DiscoveryPickKind.BecauseYouWatched,
            Seed = seed,
            LlmRationale = rationale,
        };
        return new ScoredCandidate(candidate, new DiscoveryScore { Final = finalScore });
    }

    private static SelectionStage.SelectionResult Select(params ScoredCandidate[] ranked)
        => new SelectionStage().Select(new DiscoveryContext(), ranked.ToList(), tasteFloor: 0.5);

    [Fact]
    public void LlmPicks_SkipTasteFloor_AndCarryRationaleAsReason()
    {
        // Taste floor 0.5 would kill these on the TasteMatch path; LlmPick must survive it.
        var result = Select(Llm(1, "A", 0.10, "Slow-burn sci-fi like your favorites."));

        var pick = Assert.Single(result.Picks);
        Assert.Equal(DiscoveryPickKind.LlmPick, pick.Kind);
        Assert.Equal("Slow-burn sci-fi like your favorites.", pick.Reason);
    }

    [Fact]
    public void LlmPicks_WithoutRationale_GetTheFallbackReason()
    {
        var result = Select(Llm(1, "A", 0.3, rationale: null));
        Assert.Equal("Picked for you by AI", Assert.Single(result.Picks).Reason);
    }

    [Fact]
    public void LlmPicks_ZeroFinalScoreIsDropped()
    {
        var result = Select(Llm(1, "A", 0.0, "x"));
        Assert.Empty(result.Picks);
    }

    [Fact]
    public void LlmPicks_AreCapped()
    {
        var ranked = Enumerable.Range(1, SelectionStage.LlmPickCap + 5)
            .Select(i => Llm(i, $"T{i}", 1.0 - (i * 0.01), "r"))
            .ToArray();

        var result = Select(ranked);

        Assert.Equal(SelectionStage.LlmPickCap, result.Picks.Count);
    }

    [Fact]
    public void SeededLlmPicks_GroupAsBecauseYouWatched_WithRationaleOverride()
    {
        var seed = new TasteSeed { TmdbId = 438631, MediaType = MediaType.Movie, Title = "Dune" };
        var result = Select(Llm(1, "A", 0.4, "Because Villeneuve.", seed));

        var pick = Assert.Single(result.Picks);
        Assert.Equal(DiscoveryPickKind.BecauseYouWatched, pick.Kind);
        Assert.Equal("Because Villeneuve.", pick.Reason);
        Assert.Equal(438631, pick.SeedTmdbId);
        Assert.Equal("Dune", pick.SeedTitle);
    }

    [Fact]
    public void SeededPicks_WithoutRationale_KeepTheTemplateReason()
    {
        var seed = new TasteSeed { TmdbId = 438631, MediaType = MediaType.Movie, Title = "Dune" };
        var result = Select(Llm(1, "A", 0.4, rationale: null, seed));

        Assert.Equal("Because you watched Dune", Assert.Single(result.Picks).Reason);
    }
}
