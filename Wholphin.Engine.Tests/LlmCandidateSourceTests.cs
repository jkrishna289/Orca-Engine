using System;
using System.Collections.Generic;
using Wholphin.Engine.Data.Enums;
using Wholphin.Engine.Discovery.Sources;
using Wholphin.Engine.Llm;
using Wholphin.Engine.Personalization;
using Xunit;

namespace Wholphin.Engine.Tests;

/// <summary>Unit tests for LLM source-title → taste-seed resolution and generator post-validation.</summary>
public class LlmCandidateSourceTests
{
    private static readonly List<TasteSeed> Seeds = new()
    {
        new TasteSeed { TmdbId = 438631, MediaType = MediaType.Movie, Title = "Dune", Weight = 10 },
        new TasteSeed { TmdbId = 70523, MediaType = MediaType.Series, Title = "Dark", Weight = 9 },
        new TasteSeed { TmdbId = null, MediaType = MediaType.Movie, Title = "Seedless", Weight = 5 },
    };

    [Fact]
    public void MatchSeed_ExactTitleResolves()
    {
        var seed = LlmCandidateSource.MatchSeed("Dune", Seeds, MediaType.Movie);
        Assert.NotNull(seed);
        Assert.Equal(438631, seed!.TmdbId);
    }

    [Fact]
    public void MatchSeed_IsCaseInsensitiveAndNormalized()
    {
        Assert.NotNull(LlmCandidateSource.MatchSeed("dune (2021)", Seeds, MediaType.Movie));
        Assert.NotNull(LlmCandidateSource.MatchSeed("Dark S02E12", Seeds, MediaType.Series));
    }

    [Fact]
    public void MatchSeed_RespectsMediaType()
    {
        Assert.Null(LlmCandidateSource.MatchSeed("Dune", Seeds, MediaType.Series));
    }

    [Fact]
    public void MatchSeed_SkipsSeedsWithoutTmdbIds()
    {
        Assert.Null(LlmCandidateSource.MatchSeed("Seedless", Seeds, MediaType.Movie));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Never Watched")]
    public void MatchSeed_UnattributedOrUnknownIsNull(string? sourceTitle)
    {
        Assert.Null(LlmCandidateSource.MatchSeed(sourceTitle, Seeds, MediaType.Movie));
    }

    [Fact]
    public void Validate_DropsHistoryMatchesAndDuplicates()
    {
        var history = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Dune", "The Martian" };
        var parsed = new List<LlmRecommendation>
        {
            new("Dune", 2021, "already watched (exact)", null),
            new("The Martian Chronicles", 1980, "contains a watched title (substring, both ≥5 chars)", null),
            new("Blade Runner 2049", 2017, "fresh", "Dune"),
            new("Blade Runner 2049", 2017, "duplicate", null),
        };

        var valid = LlmCandidateGenerator.Validate(parsed, history, maxResults: 10);

        var rec = Assert.Single(valid);
        Assert.Equal("Blade Runner 2049", rec.Title);
        Assert.Equal("Dune", rec.SourceTitle);
    }

    [Fact]
    public void Validate_BlanksUnknownSourceTitles()
    {
        var history = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Dune" };
        var valid = LlmCandidateGenerator.Validate(
            new List<LlmRecommendation> { new("Arrival", 2016, "x", "Some Hallucinated Source") },
            history,
            maxResults: 5);

        Assert.Null(Assert.Single(valid).SourceTitle);
    }

    [Fact]
    public void Validate_ShortTitlesNeverSubstringMatch()
    {
        var history = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Iron Man Trilogy" };
        var valid = LlmCandidateGenerator.Validate(
            new List<LlmRecommendation> { new("Her", 2013, "short title", null) },
            history,
            maxResults: 5);

        Assert.Single(valid);
    }

    [Fact]
    public void Validate_ClampsRationaleAndCapsResults()
    {
        var history = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var longRationale = new string('x', 500);
        var valid = LlmCandidateGenerator.Validate(
            new List<LlmRecommendation>
            {
                new("A Film", 2001, longRationale, null),
                new("B Film", 2002, "ok", null),
                new("C Film", 2003, "ok", null),
            },
            history,
            maxResults: 2);

        Assert.Equal(2, valid.Count);
        Assert.True(valid[0].Rationale.Length <= LlmCandidateGenerator.MaxRationaleLength + 1);
    }
}
